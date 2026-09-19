using System.Collections.Immutable;
using System.Runtime.InteropServices;
using IlRepl.Protocol;
using NuGet.Commands;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Credentials;
using NuGet.LibraryModel;
using NuGet.Packaging;
using NuGet.Packaging.Signing;
using NuGet.ProjectModel;
using NuGet.Protocol.Core.Types;
using NuGet.RuntimeModel;
using NuGet.Versioning;

namespace IlRepl.Host;

/// <summary>
/// Resolves a session's root packages together using NuGet's standard restore engine and lock format.
/// </summary>
internal static partial class PackageResolver
{
    private static readonly Lazy<bool> Credentials = new(() =>
    {
        DefaultCredentialServiceUtility.SetupDefaultCredentialService(NuGet.Common.NullLogger.Instance, nonInteractive: true);
        return true;
    });

    /// <summary>
    /// Resolves or recovers packages without requiring an installed SDK or executing package build targets.
    /// </summary>
    /// <param name="document">The source workspace and existing graph.</param>
    /// <param name="request">A new package request, or null for locked recovery.</param>
    /// <param name="directory">The directory anchoring NuGet.Config discovery.</param>
    /// <param name="cancellationToken">Cancels restore and asset reads.</param>
    /// <param name="diagnostics">Receives successful restore warnings for display by the caller.</param>
    /// <returns>A candidate source document containing the successful verified graph.</returns>
    internal static async Task<SessionDocument> ResolveAsync(
        SessionDocument document,
        string? request,
        string directory,
        ICollection<string>? diagnostics,
        CancellationToken cancellationToken)
    {
        _ = Credentials.Value;
        var roots = document.References.Where(reference => reference.Origin == "package" && reference.RequestedVersion is not null)
            .ToDictionary(reference => reference.Request, StringComparer.OrdinalIgnoreCase);
        string? requestedId = null;
        var omittedVersion = false;
        if (request is not null)
        {
            var parts = request["nuget:".Length..].Split(',', 2, StringSplitOptions.TrimEntries);
            requestedId = parts[0];
            if (!PackageIdValidator.IsValidPackageId(requestedId))
            {
                throw new InvalidDataException($"invalid NuGet package ID '{requestedId}'");
            }

            omittedVersion = parts.Length == 1 || parts[1].Length == 0;
            var range = VersionRange.Parse(omittedVersion ? "*" : parts[1]);
            roots[requestedId] = new SessionReference
            {
                Identity = roots.GetValueOrDefault(requestedId)?.Identity ?? Guid.NewGuid().ToString("N"),
                Origin = "package", Request = requestedId, RequestedVersion = range.ToNormalizedString(),
            };
        }

        if (roots.Count == 0)
        {
            return document;
        }

        if (request is null && document.PackageLock is null)
        {
            throw new InvalidDataException("the session has package requests without a lock; use .load to resolve them explicitly");
        }

        var output = Path.Combine(Path.GetTempPath(), "ilrepl-restore", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        try
        {
            var logger = new RestoreLogger();
            var settings = Settings.LoadDefaultSettings(directory);
            var packages = SettingsUtility.GetGlobalPackagesFolder(settings);
            var fallback = SettingsUtility.GetFallbackPackageFolders(settings).ToList();
            var sources = new SourceRepositoryProvider(new PackageSourceProvider(settings), Repository.Provider.GetCoreV3())
                .GetRepositories().ToList();
            var framework = ResolveFramework(document);
            var recordedLock = request is null ? PackagesLockFileFormat.Parse(document.PackageLock!, "packages.lock.json") : null;
            var runtime = RuntimeInformation.RuntimeIdentifier;
            var platformChange = recordedLock is not null && !recordedLock.Targets.Any(target => target.TargetFramework == framework
                && target.RuntimeIdentifier == runtime);
            var requests = platformChange ? PinnedRequests(recordedLock!, roots, framework)
                : roots.Values.Select(root => new LibraryDependency
            {
                LibraryRange = new LibraryRange(root.Request, VersionRange.Parse(root.RequestedVersion!), LibraryDependencyTarget.Package),
            }).ToImmutableArray();

            var project = Path.Combine(output, "ilrepl.csproj");
            var lockPath = Path.Combine(output, "packages.lock.json");
            if (request is null)
            {
                if (!platformChange)
                {
                    PackagesLockFileFormat.Write(lockPath, new PackagesLockFile(recordedLock!.Version)
                    {
                        Targets = [.. recordedLock.Targets.Where(target => target.TargetFramework == framework
                            && (string.IsNullOrEmpty(target.RuntimeIdentifier) || target.RuntimeIdentifier == runtime))],
                    });
                }
            }

            using var graphStream = typeof(PackageResolver).Assembly
                .GetManifestResourceStream("IlRepl.Host.PortableRuntimeIdentifierGraph.json")!;
            var graphPath = Path.Combine(output, "runtime-graph.json");
            await using (var graphFile = File.Create(graphPath))
            {
                await graphStream.CopyToAsync(graphFile, cancellationToken).ConfigureAwait(false);
            }

            var info = new TargetFrameworkInformation
            {
                FrameworkName = framework, RuntimeIdentifierGraphPath = graphPath,
                Dependencies = requests,
            };

            var spec = new PackageSpec([info])
            {
                Name = "ilrepl", FilePath = project,
                RuntimeGraph = new RuntimeGraph([new RuntimeDescription(RuntimeInformation.RuntimeIdentifier)]),
                RestoreMetadata = new ProjectRestoreMetadata
                {
                    ProjectStyle = ProjectStyle.PackageReference, ProjectName = "ilrepl", ProjectUniqueName = project,
                    ProjectPath = project, OutputPath = output, CacheFilePath = Path.Combine(output, "ilrepl.nuget.cache"),
                    ConfigFilePaths = settings.GetConfigFilePaths(), Sources = SettingsUtility.GetEnabledSources(settings).ToList(),
                    PackagesPath = packages, FallbackFolders = fallback, OriginalTargetFrameworks = [framework.GetShortFolderName()],
                    RestoreLockProperties = new RestoreLockProperties("true", lockPath,
                        restoreLockedMode: request is null && !platformChange),
                },
            };

            spec.RestoreMetadata.ProjectWideWarningProperties.WarningsAsErrors.Add(NuGetLogCode.NU1605);
            using var cache = new SourceCacheContext();
            var providers = new RestoreCommandProvidersCache().GetOrCreate(packages, fallback, sources, cache, logger);
            var restore = new RestoreRequest(spec, providers, cache, ClientPolicyContext.GetClientPolicy(settings, logger),
                PackageSourceMapping.GetPackageSourceMapping(settings), logger, new LockFileBuilderCache())
            {
                ProjectStyle = ProjectStyle.PackageReference, AllowNoOp = false,
            };

            restore.RequestedRuntimes.Add(RuntimeInformation.RuntimeIdentifier);
            var dependencyGraph = new DependencyGraphSpec();
            dependencyGraph.AddProject(spec);
            dependencyGraph.AddRestore(project);
            restore.DependencyGraphSpec = dependencyGraph;
            RestoreResult result;
            do
            {
                logger.Clear();
                result = await new RestoreCommand(restore).ExecuteAsync(cancellationToken).ConfigureAwait(false);
            }
            while (platformChange && PinRuntimeRequests(recordedLock!, result.LockFile, spec, framework, runtime));
            if (!result.Success)
            {
                throw new InvalidDataException("NuGet restore failed: " + string.Join(Environment.NewLine, logger.Messages));
            }

            var target = result.LockFile.GetTarget(framework, RuntimeInformation.RuntimeIdentifier)
                ?? throw new InvalidDataException("NuGet produced no runtime assets for " + RuntimeInformation.RuntimeIdentifier);
            var pathResolver = new FallbackPackagePathResolver(packages, fallback);
            var assets = document.Assets.ToDictionary(asset => asset.Hash, StringComparer.Ordinal);
            var libraries = target.Libraries.Where(library => library.Type == "package").ToArray();
            var identities = libraries.ToDictionary(library => library.Name!, library =>
                document.References.FirstOrDefault(reference => reference.Origin == "package"
                    && reference.Request.Equals(library.Name, StringComparison.OrdinalIgnoreCase))?.Identity
                ?? roots.GetValueOrDefault(library.Name!)?.Identity ?? Guid.NewGuid().ToString("N"), StringComparer.OrdinalIgnoreCase);
            var references = new List<SessionReference>();
            foreach (var library in libraries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var packagePath = pathResolver.GetPackageDirectory(library.Name!, library.Version!)
                    ?? throw new InvalidDataException($"restored package '{library.Name}' is unavailable");
                var selected = new List<SessionReferenceAsset>();
                async Task AddAssets(IEnumerable<LockFileItem> items, string kind)
                {
                    foreach (var item in items.Where(item => Path.GetFileName(item.Path) != "_._"))
                    {
                        var path = Path.GetFullPath(Path.Combine(packagePath, item.Path.Replace('/', Path.DirectorySeparatorChar)));
                        if (!path.StartsWith(Path.GetFullPath(packagePath) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                        {
                            throw new InvalidDataException("NuGet returned an asset outside its package directory");
                        }

                        var asset = await DependencyAsset.ReadAsync(path, kind, assets, cancellationToken).ConfigureAwait(false);
                        var segments = item.Path.Split('/');
                        selected.Add(asset with
                        {
                            PackagePath = item.Path,
                            Rid = segments.Length > 2 && segments[0] == "runtimes" ? segments[1] : null,
                        });
                    }
                }

                await AddAssets(library.RuntimeAssemblies, "managed").ConfigureAwait(false);
                await AddAssets(library.ResourceAssemblies, "satellite").ConfigureAwait(false);
                await AddAssets(library.NativeLibraries, "native").ConfigureAwait(false);
                if (library.RuntimeAssemblies.Count == 0)
                {
                    await AddAssets(library.CompileTimeAssemblies, "reference").ConfigureAwait(false);
                }

                var packageFiles = result.LockFile.Libraries
                    .First(item => item.Name == library.Name && item.Version == library.Version).Files;
                var foreignNatives = packageFiles.Where(file => file.StartsWith("runtimes/", StringComparison.Ordinal)
                    && file.Contains("/native/", StringComparison.Ordinal))
                    .Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (library.NativeLibraries.Count == 0 && selected.All(asset => asset.Kind != "managed") && foreignNatives.Count != 0)
                {
                    throw new InvalidDataException($"package '{library.Name}' has no compatible native assets for "
                        + RuntimeInformation.RuntimeIdentifier);
                }

                references.Add(new SessionReference
                {
                    Identity = identities[library.Name!], Origin = "package", Request = library.Name!,
                    RequestedVersion = omittedVersion && library.Name!.Equals(requestedId, StringComparison.OrdinalIgnoreCase)
                        ? new VersionRange(library.Version!).ToNormalizedString()
                        : roots.GetValueOrDefault(library.Name!)?.RequestedVersion,
                    Version = library.Version!.ToNormalizedString(), Framework = framework.GetShortFolderName(), Assets = [.. selected],
                    Frameworks = [.. library.FrameworkReferences],
                    Dependencies = [.. library.Dependencies.Where(dependency => identities.ContainsKey(dependency.Id))
                        .Select(dependency => identities[dependency.Id])],
                });
            }

            if (recordedLock is not null)
            {
                ValidateLockedPackages(recordedLock, result.LockFile, framework);
            }

            await result.CommitAsync(logger, cancellationToken).ConfigureAwait(false);
            if (recordedLock is not null)
            {
                MergeLockedTargets(lockPath, recordedLock, roots, framework, runtime, platformChange);
                if (platformChange)
                {
                    diagnostics?.Add("restored locked package versions for " + runtime);
                }
            }

            if (omittedVersion && requestedId is not null)
            {
                var selectedVersion = references.Single(reference => reference.Request.Equals(requestedId,
                    StringComparison.OrdinalIgnoreCase)).Version!;
                var pinnedLock = PackagesLockFileFormat.Read(lockPath);
                foreach (var dependency in pinnedLock.Targets.SelectMany(target => target.Dependencies)
                    .Where(dependency => dependency.Id.Equals(requestedId, StringComparison.OrdinalIgnoreCase)
                        && dependency.Type == PackageDependencyType.Direct))
                {
                    dependency.RequestedVersion = new VersionRange(NuGetVersion.Parse(selectedVersion));
                }

                PackagesLockFileFormat.Write(lockPath, pinnedLock);
            }

            foreach (var message in logger.Messages.Distinct(StringComparer.Ordinal))
            {
                diagnostics?.Add(message);
            }

            var packageLock = await File.ReadAllTextAsync(lockPath, cancellationToken).ConfigureAwait(false);
            var entries = document.Entries.ToList();
            if (requestedId is not null && !entries.Any(entry => entry.Kind == SessionEntryKind.Reference
                && entry.Reference == identities[requestedId]))
            {
                entries.Add(new SessionEntry
                {
                    Kind = SessionEntryKind.Reference,
                    Reference = identities[requestedId],
                    Number = document.Cells.Select(cell => cell.Number).DefaultIfEmpty(0).Max() + 1,
                    Source = [".load " + request],
                });
            }

            return document with
            {
                References = [.. document.References.Where(reference => reference.Origin != "package"), .. references],
                Assets = [.. assets.Values], Entries = [.. entries], PackageLock = packageLock,
            };
        }
        finally
        {
            Directory.Delete(output, recursive: true);
        }
    }
}
