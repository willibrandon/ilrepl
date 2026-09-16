using IlRepl.Protocol;

namespace IlRepl.Host;

/// <summary>
/// Stores desktop session documents atomically and resolves their verified dependency images.
/// </summary>
public sealed partial class SessionFileStore
{
    private readonly string _cacheDirectory;

    /// <summary>
    /// Initializes document storage with an optional isolated asset cache.
    /// </summary>
    /// <param name="cacheDirectory">The asset cache, or null for the user's ilrepl cache.</param>
    public SessionFileStore(string? cacheDirectory = null)
    {
        _cacheDirectory = cacheDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ilrepl", "assets");
    }

    /// <summary>
    /// Reads and validates a document before resolving relative paths and cached asset hashes.
    /// </summary>
    /// <param name="path">The requested session file.</param>
    /// <param name="cancellationToken">Cancels file reads.</param>
    /// <returns>A source document with verified available images and absolute desktop locators.</returns>
    public async Task<SessionDocument> ReadAsync(string path, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)!;
        byte[] bytes;
        try
        {
            bytes = await ReadBoundedAsync(fullPath, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException exception)
        {
            throw new FileNotFoundException("session file does not exist: " + fullPath, fullPath, exception);
        }
        catch (DirectoryNotFoundException exception)
        {
            throw new FileNotFoundException("session file does not exist: " + fullPath, fullPath, exception);
        }
        var document = SessionCodec.Read(bytes);
        var assets = document.Assets.ToDictionary(asset => asset.Hash, StringComparer.Ordinal);
        var references = document.References.Select(reference => ResolveLocators(reference, directory)).ToArray();
        foreach (var asset in references.SelectMany(reference => reference.Assets))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (assets.ContainsKey(asset.Hash))
            {
                continue;
            }

            foreach (var candidate in new[] { Path.Combine(_cacheDirectory, asset.Hash), asset.Path })
            {
                if (candidate is null || !File.Exists(candidate))
                {
                    continue;
                }

                byte[] image;
                try
                {
                    image = await ReadBoundedAsync(candidate, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    // An unavailable candidate must not prevent verified fallback or reopening its source without that asset.
                    continue;
                }

                if (SessionCodec.Hash(image) == asset.Hash)
                {
                    assets[asset.Hash] = new SessionAsset { Hash = asset.Hash, Image = image };
                    break;
                }
            }
        }

        return document with { References = references, Assets = [.. assets.Values] };
    }

    /// <summary>
    /// Writes a complete snapshot through a sibling temporary file before replacing the destination.
    /// </summary>
    /// <param name="path">The destination path.</param>
    /// <param name="document">The captured source revision.</param>
    /// <param name="embed">Whether to include all available non-framework images.</param>
    /// <param name="cancellationToken">Cancels preparation and writing before the atomic replacement.</param>
    /// <returns>The absolute associated session path.</returns>
    public async Task<string> WriteAsync(string path, SessionDocument document, bool embed, CancellationToken cancellationToken)
    {
        SessionCodec.Validate(document);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)!;
        var baselines = document.References.Where(reference => reference.Origin == "baseline")
            .SelectMany(reference => reference.Assets).Select(asset => asset.Hash).ToHashSet(StringComparer.Ordinal);
        var portable = document with
        {
            References = [.. document.References.Select(reference => PortableLocators(reference, directory))],
            Assets = embed ? document.Assets : [.. document.Assets.Where(asset => baselines.Contains(asset.Hash))],
        };
        var bytes = SessionCodec.Write(portable);
        Directory.CreateDirectory(_cacheDirectory);
        foreach (var reference in document.References.Where(reference => reference.Origin is "project" or "assembly"))
        {
            await RememberLocatorsAsync(reference, cancellationToken).ConfigureAwait(false);
        }
        foreach (var asset in document.Assets)
        {
            await AtomicWriteAsync(Path.Combine(_cacheDirectory, asset.Hash), asset.Image, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            Directory.CreateDirectory(directory);
            await AtomicWriteAsync(fullPath, bytes, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"could not save session '{fullPath}': {exception.Message}", exception);
        }
        return fullPath;
    }

    private static async Task<byte[]> ReadBoundedAsync(string path, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read | FileShare.Delete, 8192, useAsync: true);
        if (input.Length > SessionCodec.FileLimit)
        {
            throw new InvalidDataException("the session or dependency exceeds the 64 MiB file limit");
        }

        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (output.Length + read > SessionCodec.FileLimit)
            {
                throw new InvalidDataException("the session or dependency exceeds the 64 MiB file limit");
            }

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    private static async Task AtomicWriteAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                8192, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Move(temporary, path);
            }
            catch (IOException) when (File.Exists(path))
            {
                File.Replace(temporary, path, destinationBackupFileName: null);
            }
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
