using System.Text;
using System.Text.Json;

namespace IlRepl.Protocol;

/// <summary>
/// Keeps portable dependency locators separate from private machine-specific recovery hints.
/// </summary>
public static partial class SessionSnapshotStore
{
    private static SessionReference PortableLocators(SessionReference reference, string directory)
    {
        var root = RepositoryRoot(directory) ?? directory;
        string? Portable(string? path)
        {
            if (path is null) return null;
            var full = Path.GetFullPath(path);
            var relative = Path.GetRelativePath(root, full);
            return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !Path.IsPathRooted(relative)
                ? Path.GetRelativePath(directory, full).Replace(Path.DirectorySeparatorChar, '/') : null;
        }

        return reference with
        {
            Request = reference.Origin == "project" || (reference.Origin == "assembly" && reference.Assets.Length != 0)
                ? Portable(reference.Request) ?? Path.GetFileName(reference.Request) : reference.Request,
            Assets = [.. reference.Assets.Select(asset => asset with
            {
                Path = asset.PackagePath is null ? Portable(asset.Path) : null,
            })],
        };
    }

    private static string? RepositoryRoot(string directory)
    {
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
        {
            var git = Path.Combine(current.FullName, ".git");
            if (Directory.Exists(git) || File.Exists(git)) return current.FullName;
        }

        return null;
    }

    /// <summary>
    /// Reads private recovery hints without placing machine-specific paths in a shared session document.
    /// </summary>
    /// <param name="cacheDirectory">The desktop asset cache containing private locator records.</param>
    /// <param name="reference">The reference whose origin, identity, and hashes identify its record.</param>
    /// <returns>Available private locators, or an empty map when the cache is unavailable or malformed.</returns>
    public static Dictionary<string, string> ReadLocators(string cacheDirectory, SessionReference reference)
    {
        var path = LocatorPath(cacheDirectory, reference);
        if (!File.Exists(path)) return [];
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(path), ProtocolJsonContext.Default.DictionaryStringString) ?? [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    private static async Task RememberLocatorsAsync(string cacheDirectory, SessionReference reference,
        CancellationToken cancellationToken)
    {
        if (reference.Assets.Length == 0 && reference.Origin != "project") return;
        var paths = new Dictionary<string, string> { ["request"] = Path.GetFullPath(reference.Request) };
        foreach (var asset in reference.Assets.Where(asset => asset.Path is not null)) paths[asset.Hash] = Path.GetFullPath(asset.Path!);
        var path = LocatorPath(cacheDirectory, reference);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await AtomicWriteAsync(path, JsonSerializer.SerializeToUtf8Bytes(paths, ProtocolJsonContext.Default.DictionaryStringString),
            cancellationToken).ConfigureAwait(false);
    }

    private static string LocatorPath(string cacheDirectory, SessionReference reference)
    {
        var key = reference.Origin + "/" + reference.Identity + "/" + Path.GetFileName(reference.Request)
            + "/" + string.Join('/', reference.Assets.Select(asset => asset.Hash).Order(StringComparer.Ordinal));
        return Path.Combine(cacheDirectory, "locators", SessionCodec.Hash(Encoding.UTF8.GetBytes(key)) + ".json");
    }
}
