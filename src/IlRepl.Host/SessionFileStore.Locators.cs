using System.Text;
using System.Text.Json;
using IlRepl.Protocol;
using NuGet.Configuration;
using NuGet.Packaging;
using NuGet.Versioning;

namespace IlRepl.Host;

/// <summary>
/// Keeps portable dependency locators separate from private machine-specific recovery hints.
/// </summary>
public sealed partial class SessionFileStore
{
    private SessionReference ResolveLocators(SessionReference reference, string directory)
    {
        var remembered = ReadLocators(reference);
        var request = reference.Request;
        if (reference.Origin == "project" || (reference.Origin == "assembly" && reference.Assets.Length != 0))
        {
            request = Path.GetFullPath(request.Replace('\\', Path.DirectorySeparatorChar), directory);
            if (!File.Exists(request) && remembered.TryGetValue("request", out var local)) request = local;
        }

        string? package = null;
        if (reference.Origin == "package" && reference.Assets.Any(asset => asset.PackagePath is not null))
        {
            if (!PackageIdValidator.IsValidPackageId(reference.Request) || !NuGetVersion.TryParse(reference.Version, out var version))
            {
                throw new InvalidDataException("invalid package identity for portable asset lookup: " + reference.Request);
            }

            var settings = Settings.LoadDefaultSettings(directory);
            package = new FallbackPackagePathResolver(SettingsUtility.GetGlobalPackagesFolder(settings),
                SettingsUtility.GetFallbackPackageFolders(settings)).GetPackageDirectory(reference.Request, version);
        }

        return reference with
        {
            Request = request,
            Assets = [.. reference.Assets.Select(asset => asset with
            {
                Path = asset.PackagePath is not null ? package is null ? null
                    : Path.Combine(package, asset.PackagePath.Replace('/', Path.DirectorySeparatorChar))
                    : asset.Path is not null ? Path.GetFullPath(asset.Path.Replace('\\', Path.DirectorySeparatorChar), directory)
                    : remembered.GetValueOrDefault(asset.Hash),
            })],
        };
    }

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

    private Dictionary<string, string> ReadLocators(SessionReference reference)
    {
        var path = LocatorPath(reference);
        if (!File.Exists(path)) return [];
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    private async Task RememberLocatorsAsync(SessionReference reference, CancellationToken cancellationToken)
    {
        if (reference.Assets.Length == 0 && reference.Origin != "project") return;
        var paths = new Dictionary<string, string> { ["request"] = Path.GetFullPath(reference.Request) };
        foreach (var asset in reference.Assets.Where(asset => asset.Path is not null)) paths[asset.Hash] = Path.GetFullPath(asset.Path!);
        var path = LocatorPath(reference);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await AtomicWriteAsync(path, JsonSerializer.SerializeToUtf8Bytes(paths), cancellationToken).ConfigureAwait(false);
    }

    private string LocatorPath(SessionReference reference)
    {
        var key = reference.Origin + "/" + reference.Identity + "/" + Path.GetFileName(reference.Request)
            + "/" + string.Join('/', reference.Assets.Select(asset => asset.Hash).Order(StringComparer.Ordinal));
        return Path.Combine(_cacheDirectory, "locators", SessionCodec.Hash(Encoding.UTF8.GetBytes(key)) + ".json");
    }
}
