using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace IlRepl.Tui;

/// <summary>
/// History in a file, in the shape pgcli keeps its own: a timestamp line, then each line of the
/// entry with a plus in front, then a blank line. The file is only ever appended to, and every
/// read and append happens under a lock file beside it, so two sessions never open the file at
/// the same length and overwrite each other. A session that cannot take the lock in time keeps
/// its entry in memory and says so once.
/// </summary>
public sealed class FileHistoryStore : IHistoryStore
{
    /// <summary>
    /// How many entries are read back into memory; the file keeps them all.
    /// </summary>
    public const int MaxEntries = 1000;

    private static readonly TimeSpan DefaultLockTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan LockRetry = TimeSpan.FromMilliseconds(10);

    private readonly TimeSpan _lockTimeout;

    /// <summary>
    /// Initializes a store over a file.
    /// </summary>
    /// <param name="path">The history file.</param>
    /// <param name="lockTimeout">How long to wait for another session's lock, or null for two seconds.</param>
    public FileHistoryStore(string path, TimeSpan? lockTimeout = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        Path = path;
        LockPath = path + ".lock";
        _lockTimeout = lockTimeout ?? DefaultLockTimeout;
    }

    /// <summary>
    /// The history file.
    /// </summary>
    public string Path { get; }

    /// <summary>
    /// The lock file beside it.
    /// </summary>
    public string LockPath { get; }

    /// <inheritdoc />
    public string? Problem { get; private set; }

    /// <summary>
    /// Where the history file lives: <c>$XDG_CONFIG_HOME/ilrepl/history</c> when the variable is
    /// set, else the local application data folder on Windows, else <c>~/.config/ilrepl/history</c>.
    /// </summary>
    /// <returns>The path.</returns>
    public static string DefaultPath()
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (!string.IsNullOrEmpty(xdg))
        {
            return System.IO.Path.Combine(xdg, "ilrepl", "history");
        }

        if (OperatingSystem.IsWindows())
        {
            return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ilrepl", "history");
        }

        return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "ilrepl", "history");
    }

    /// <summary>
    /// Reads the entries out of a file's text. A line with a plus in front is a line of an entry;
    /// any other line ends the entry. A record cut off before its final newline is dropped.
    /// </summary>
    /// <param name="content">The file's text.</param>
    /// <returns>The entries, oldest first.</returns>
    public static IReadOnlyList<string> Parse(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Length > 0 && !content.EndsWith('\n'))
        {
            var lastHeader = content.LastIndexOf("\n# ", StringComparison.Ordinal);
            content = lastHeader < 0 ? "" : content[..(lastHeader + 1)];
        }

        var entries = new List<string>();
        List<string>? current = null;
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith('+'))
            {
                current ??= [];
                current.Add(line[1..]);
            }
            else if (current is not null)
            {
                entries.Add(string.Join('\n', current));
                current = null;
            }
        }

        if (current is not null)
        {
            entries.Add(string.Join('\n', current));
        }

        return entries;
    }

    /// <summary>
    /// Writes one entry the way the file holds it.
    /// </summary>
    /// <param name="entry">The entry, lines separated by newlines.</param>
    /// <param name="at">When it was entered.</param>
    /// <returns>The record, blank line first.</returns>
    public static string Format(string entry, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var record = new StringBuilder();
        record.Append("\n# ").Append(at.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture)).Append('\n');
        foreach (var line in entry.Split('\n'))
        {
            record.Append('+').Append(line.TrimEnd('\r')).Append('\n');
        }

        return record.ToString();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(Path))
            {
                return [];
            }

            using var held = await LockAsync(cancellationToken).ConfigureAwait(false);
            if (held is null)
            {
                return [];
            }

            var content = await File.ReadAllTextAsync(Path, cancellationToken).ConfigureAwait(false);
            var entries = Parse(content);
            return entries.Count > MaxEntries ? entries.Skip(entries.Count - MaxEntries).ToList() : entries;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Problem = ex.Message;
            return [];
        }
    }

    /// <inheritdoc />
    public async Task AppendAsync(string entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        try
        {
            var directory = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(directory))
            {
                CreateDirectory(directory);
            }

            using var held = await LockAsync(cancellationToken).ConfigureAwait(false);
            if (held is null)
            {
                return;
            }

            // Every line typed ends up here, string literals included, so the file is the
            // owner's alone: created that way, and an older file tightened before it grows.
            using var stream = new FileStream(Path, OwnerOnly(new FileStreamOptions { Mode = FileMode.Append, Access = FileAccess.Write, Share = FileShare.Read }));
            Tighten(Path);
            var bytes = Encoding.UTF8.GetBytes(Format(entry, DateTimeOffset.Now));
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Problem = ex.Message;
        }
    }

    private static void CreateDirectory(string directory)
    {
        if (OperatingSystem.IsWindows() || Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }
        else
        {
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static FileStreamOptions OwnerOnly(FileStreamOptions options)
    {
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        return options;
    }

    private static void Tighten(string path)
    {
        if (!OperatingSystem.IsWindows() && File.GetUnixFileMode(path) != (UnixFileMode.UserRead | UnixFileMode.UserWrite))
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private async Task<FileStream?> LockAsync(CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            try
            {
                return new FileStream(LockPath, OwnerOnly(new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None, BufferSize = 1 }));
            }
            catch (IOException) when (Stopwatch.GetElapsedTime(started) < _lockTimeout)
            {
                await Task.Delay(LockRetry, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                Problem = $"another ilrepl holds {LockPath}";
                return null;
            }
        }
    }
}
