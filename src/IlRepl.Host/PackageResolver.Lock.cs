using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Versioning;
using IlRepl.Protocol;
using NuGet.Frameworks;
using NuGet.LibraryModel;
using NuGet.ProjectModel;
using NuGet.Versioning;

namespace IlRepl.Host;

/// <summary>
/// Preserves locked package identities while selecting assets for the current execution platform.
/// </summary>
internal static partial class PackageResolver
{
    /// <summary>
    /// Gets the framework targeted by the shipped host independently of runtime roll-forward.
    /// </summary>
    internal static NuGetFramework HostFramework => NuGetFramework.ParseFrameworkName(
        typeof(PackageResolver).Assembly.GetCustomAttribute<TargetFrameworkAttribute>()!.FrameworkName,
        DefaultFrameworkNameProvider.Instance);

    private static NuGetFramework ResolveFramework(SessionDocument document)
    {
        var frameworks = document.References.Where(reference => reference.Origin == "package" && reference.Framework is not null)
            .Select(reference => reference.Framework!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var framework = frameworks.Length == 0 ? HostFramework : NuGetFramework.ParseFolder(frameworks[0]);
        if (frameworks.Length > 1 || !DefaultCompatibilityProvider.Instance.IsCompatible(HostFramework, framework))
        {
            throw new InvalidDataException("the recorded package framework is incompatible with " + HostFramework.GetShortFolderName());
        }

        return framework;
    }

    private static ImmutableArray<LibraryDependency> PinnedRequests(
        PackagesLockFile recorded,
        Dictionary<string, SessionReference> roots,
        NuGetFramework framework)
    {
        var portable = recorded.Targets.SingleOrDefault(target => target.TargetFramework == framework
            && string.IsNullOrEmpty(target.RuntimeIdentifier))
            ?? throw new InvalidDataException("the package lock has no portable target for " + framework.GetShortFolderName());
        var direct = portable.Dependencies.Where(dependency => dependency.Type == PackageDependencyType.Direct).ToArray();
        if (direct.Length != roots.Count || direct.Any(dependency => !roots.TryGetValue(dependency.Id, out var root)
            || !VersionRange.Parse(root.RequestedVersion!).Equals(dependency.RequestedVersion)))
        {
            throw new InvalidDataException("NuGet locked restore requires the recorded package requests; "
                + "use .load to explicitly adopt a changed package graph");
        }

        return [.. portable.Dependencies.Where(dependency => dependency.Type != PackageDependencyType.Project)
            .Select(dependency => new LibraryDependency
            {
                LibraryRange = new LibraryRange(dependency.Id,
                    new VersionRange(dependency.ResolvedVersion, true, dependency.ResolvedVersion, true), LibraryDependencyTarget.Package),
            })];
    }

    private static void ValidateLockedPackages(PackagesLockFile recorded, LockFile restored, NuGetFramework framework)
    {
        var portable = recorded.Targets.Single(target => target.TargetFramework == framework
            && string.IsNullOrEmpty(target.RuntimeIdentifier));
        foreach (var dependency in portable.Dependencies.Where(dependency => dependency.Type != PackageDependencyType.Project))
        {
            var selected = restored.Libraries.SingleOrDefault(library =>
                library.Name.Equals(dependency.Id, StringComparison.OrdinalIgnoreCase) && library.Version == dependency.ResolvedVersion);
            if (selected is null || selected.Version != dependency.ResolvedVersion || selected.Sha512 != dependency.ContentHash)
            {
                throw new InvalidDataException("NuGet locked restore cannot change " + dependency.Id + " " + dependency.ResolvedVersion
                    + "; use .load to explicitly adopt a changed package graph");
            }
        }

        var runtimeDependencies = RuntimeDependencies(recorded, framework);
        foreach (var selected in restored.Libraries)
        {
            if (runtimeDependencies.TryGetValue(selected.Name, out var known)
                && !known.Any(dependency => selected.Version == dependency.ResolvedVersion && selected.Sha512 == dependency.ContentHash))
            {
                throw new InvalidDataException("NuGet locked restore cannot change runtime package " + selected.Name
                    + "; use .load to explicitly adopt a changed package graph");
            }
        }
    }

    private static bool PinRuntimeRequests(
        PackagesLockFile recorded,
        LockFile restored,
        PackageSpec spec,
        NuGetFramework framework,
        string runtime)
    {
        var known = RuntimeDependencies(recorded, framework);
        var info = spec.TargetFrameworks.Single(target => target.FrameworkName == framework);
        var target = restored.GetTarget(framework, runtime);
        if (target is null)
        {
            return false;
        }

        var pins = new List<LibraryDependency>();
        foreach (var selected in target.Libraries)
        {
            if (selected.Name is null || !known.TryGetValue(selected.Name, out var versions)
                || versions.Any(dependency => dependency.ResolvedVersion == selected.Version))
            {
                continue;
            }

            var ranges = target.Libraries.SelectMany(library => library.Dependencies)
                .Where(dependency => dependency.Id.Equals(selected.Name, StringComparison.OrdinalIgnoreCase))
                .Select(dependency => dependency.VersionRange).ToArray();
            var available = versions.Select(dependency => dependency.ResolvedVersion).Distinct().Order().ToArray();
            var candidates = available.Where(version => ranges.All(range => range.Satisfies(version))).ToArray();
            if (candidates.Length == 0)
            {
                candidates = available;
            }

            if (info.Dependencies.Any(dependency => dependency.Name.Equals(selected.Name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var pinned = ranges.Any(range => range.IsFloating) ? candidates[^1] : candidates[0];
            pins.Add(new LibraryDependency
            {
                LibraryRange = new LibraryRange(selected.Name, new VersionRange(pinned, true, pinned, true),
                    LibraryDependencyTarget.Package),
            });
        }

        if (pins.Count != 0)
        {
            spec.TargetFrameworks[spec.TargetFrameworks.IndexOf(info)] = new TargetFrameworkInformation(info)
            {
                Dependencies = info.Dependencies.AddRange(pins),
            };
        }

        return pins.Count != 0;
    }

    private static Dictionary<string, LockFileDependency[]> RuntimeDependencies(PackagesLockFile recorded, NuGetFramework framework)
        => recorded.Targets.Where(target => target.TargetFramework == framework)
            .SelectMany(target => target.Dependencies).Where(dependency => dependency.Type != PackageDependencyType.Project)
            .GroupBy(dependency => dependency.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);

    private static void MergeLockedTargets(
        string path,
        PackagesLockFile recorded,
        Dictionary<string, SessionReference> roots,
        NuGetFramework framework,
        string runtime,
        bool platformChange)
    {
        var restored = PackagesLockFileFormat.Read(path);
        foreach (var dependency in restored.Targets.SelectMany(target => target.Dependencies))
        {
            if (roots.TryGetValue(dependency.Id, out var root))
            {
                dependency.Type = PackageDependencyType.Direct;
                dependency.RequestedVersion = VersionRange.Parse(root.RequestedVersion!);
            }
            else if (dependency.Type == PackageDependencyType.Direct)
            {
                dependency.Type = PackageDependencyType.Transitive;
                dependency.RequestedVersion = null;
            }
        }

        if (platformChange)
        {
            var portable = restored.Targets.Single(target => target.TargetFramework == framework
                && string.IsNullOrEmpty(target.RuntimeIdentifier));
            var original = recorded.Targets.Single(target => target.TargetFramework == framework
                && string.IsNullOrEmpty(target.RuntimeIdentifier));
            var current = restored.Targets.Single(target => target.TargetFramework == framework && target.RuntimeIdentifier == runtime);
            var portableIds = original.Dependencies.Select(dependency => dependency.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var dependency in portable.Dependencies.Where(dependency => !portableIds.Contains(dependency.Id)))
            {
                if (!current.Dependencies.Any(item => item.Id.Equals(dependency.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    current.Dependencies.Add(dependency);
                }
            }

            restored.Targets.Remove(portable);
            restored.Targets.Insert(0, original);
        }

        foreach (var target in recorded.Targets.Where(target => target.TargetFramework != framework
            || (!string.IsNullOrEmpty(target.RuntimeIdentifier) && target.RuntimeIdentifier != runtime)))
        {
            restored.Targets.Add(target);
        }

        PackagesLockFileFormat.Write(path, restored);
    }
}
