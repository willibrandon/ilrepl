using System.Globalization;
using System.Reflection;
using IlRepl.Protocol;
using Mono.Cecil;

namespace IlRepl.Engine;

/// <summary>
/// Retains loaded and adjacent satellites that an original resolves without a static assembly reference.
/// </summary>
public static partial class ComparisonCapture
{
    /// <summary>
    /// Retains the loaded and adjacent satellite graph used by an isolated execution.
    /// </summary>
    /// <param name="session">The declaration context.</param>
    /// <param name="dependencies">The immutable images to extend.</param>
    /// <param name="source">The selected dependency binding graph.</param>
    internal static void CaptureSatellites(Session session, Dictionary<string, ComparisonAssembly> dependencies, TypeResolver source)
    {
        var parents = dependencies.Values.ToArray();
        var names = parents.Select(parent => new AssemblyName(parent.Name)).ToArray();
        foreach (var assembly in source.Assemblies.ToArray())
        {
            var name = assembly.GetName();
            if (string.IsNullOrEmpty(name.CultureName) || !names.Any(parent =>
                string.Equals(name.Name, parent.Name + ".resources", StringComparison.OrdinalIgnoreCase)
                && name.GetPublicKeyToken().AsSpan().SequenceEqual(parent.GetPublicKeyToken())))
            {
                continue;
            }

            CaptureDependency(name.FullName, session, dependencies, source: source);
        }

        foreach (var parent in parents)
        {
            if (parent.OriginalLocation is null)
            {
                continue;
            }

            var paths = ComparisonSatelliteFiles.Paths(parent);
            dependencies[parent.Name] = parent with { OriginalSatelliteFiles = paths };
            foreach (var path in paths)
            {
                CaptureSatelliteFile(parent, path, dependencies);
            }
        }
    }

    private static void CaptureSatelliteFile(
        ComparisonAssembly parent,
        string path,
        Dictionary<string, ComparisonAssembly> dependencies)
    {
        try
        {
            var image = File.ReadAllBytes(path);
            using var stream = new MemoryStream(image, writable: false);
            using var module = ModuleDefinition.ReadModule(stream);
            var name = module.Assembly?.Name ?? throw new BadImageFormatException("the satellite has no assembly identity");
            var identity = new AssemblyName(name.FullName);
            var owner = new AssemblyName(parent.Name);
            var culture = Path.GetFileName(Path.GetDirectoryName(path)!);
            if (!string.Equals(identity.Name, owner.Name + ".resources", StringComparison.OrdinalIgnoreCase)
                || !identity.GetPublicKeyToken().AsSpan().SequenceEqual(owner.GetPublicKeyToken())
                || string.IsNullOrEmpty(identity.CultureName)
                || !string.Equals(CultureInfo.GetCultureInfo(identity.CultureName).Name, culture, StringComparison.OrdinalIgnoreCase))
            {
                throw new BadImageFormatException("the satellite identity does not match its parent and culture directory");
            }

            dependencies.TryAdd(identity.FullName, new ComparisonAssembly(identity.FullName, image)
            {
                OriginalLocation = path,
                IsCollectible = parent.IsCollectible,
            });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or BadImageFormatException
            or ArgumentException)
        {
            throw new ReplException($"cannot capture satellite assembly '{path}': {exception.Message}", exception);
        }
    }
}
