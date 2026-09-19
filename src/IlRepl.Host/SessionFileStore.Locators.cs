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
        var remembered = SessionSnapshotStore.ReadLocators(_cacheDirectory, reference);
        var request = reference.Request;
        if (reference.Origin == "project" || (reference.Origin == "assembly" && reference.Assets.Length != 0))
        {
            request = Path.GetFullPath(request.Replace('\\', Path.DirectorySeparatorChar), directory);
            if (!File.Exists(request) && remembered.TryGetValue("request", out var local))
            {
                request = local;
            }
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
}
