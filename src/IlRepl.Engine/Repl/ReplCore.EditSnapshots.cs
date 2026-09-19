using System.Reflection;
using System.Runtime.Loader;
using IlRepl.Engine;
using IlRepl.Protocol;

namespace IlRepl.Repl;

/// <summary>
/// Retains immutable original metadata and dependency images independently of the current session reference graph.
/// </summary>
public sealed partial class ReplCore
{
    private SessionEditSnapshot CaptureEdit(MethodEdit edit)
    {
        var reference = _references.FirstOrDefault(candidate => candidate.Origin == "baseline" && candidate.Request == edit.Fingerprint);
        if (reference is not null && _sourceEntries.FirstOrDefault(entry => entry.Edit?.Fingerprint == edit.Fingerprint)?.Edit is { } saved)
        {
            return saved with { OpensBlock = _editBlock is not null };
        }

        var captured = new Dictionary<string, SessionReferenceAsset>(StringComparer.Ordinal);
        var visited = new HashSet<Assembly>();
        void Capture(Assembly assembly, TypeResolver resolver)
        {
            if (!visited.Add(assembly) || IsShared(assembly))
            {
                return;
            }

            byte[]? image = null;
            if (SessionAssemblies.TryGetDefinition(assembly, out var definition))
            {
                image = definition.Image;
            }
            else if (resolver.TryGetImage(assembly, out var retained))
            {
                image = retained;
            }
            else if (!assembly.IsDynamic && File.Exists(assembly.Location))
            {
                image = File.ReadAllBytes(assembly.Location);
            }

            if (image is null)
            {
                throw new ReplException("the original assembly image is unavailable: " + assembly.FullName);
            }

            var hash = SessionCodec.Hash(image);
            captured[hash] = new SessionReferenceAsset
            {
                Name = assembly.FullName!,
                Hash = hash,
                Mvid = assembly.ManifestModule.ModuleVersionId.ToString(),
            };

            _assets.TryAdd(hash, new SessionAsset { Hash = hash, Image = image });
            if (definition is not null)
            {
                foreach (var dependency in definition.Dependencies)
                {
                    Capture(dependency.Assembly, resolver);
                }
            }

            var context = AssemblyLoadContext.GetLoadContext(assembly);
            foreach (var name in assembly.GetReferencedAssemblies())
            {
                try
                {
                    var dependency = context?.LoadFromAssemblyName(name) ?? Assembly.Load(name);
                    Capture(dependency, resolver);
                }
                catch (FileNotFoundException)
                {
                    // Metadata can include unused references; a reachable unresolved method is diagnosed when parsed.
                }
            }
        }

        var bindings = edit.Baseline.CaptureSnapshot(Capture);
        if (edit.Baseline.Definition is { } baseline)
        {
            Capture(baseline.Assembly, Session.Resolver);
        }

        foreach (var (name, path) in edit.Baseline.SourceResolver.NativeLibraries)
        {
            var image = File.ReadAllBytes(path);
            var hash = SessionCodec.Hash(image);
            var descriptor = _references.SelectMany(candidate => candidate.Assets)
                .FirstOrDefault(asset => asset.Kind == "native" && asset.Hash == hash && Path.GetFileName(asset.Name) == name)
                ?? throw new ReplException("the original native dependency is unavailable: " + name);
            captured[hash] = descriptor with { PackagePath = null };
            _assets.TryAdd(hash, new SessionAsset { Hash = hash, Image = image });
        }

        reference = new SessionReference { Origin = "baseline", Request = edit.Fingerprint, Assets = [.. captured.Values] };
        _references.Add(reference);
        return bindings with
        {
            Name = edit.Name,
            Reference = edit.Reference,
            Fingerprint = edit.Fingerprint,
            Source = edit.Baseline.Selected.Source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'),
            OpensBlock = _editBlock is not null,
            BaselineReference = reference.Identity,
        };
    }

    private MethodEdit RestoreEditSnapshot(SessionEditSnapshot snapshot)
    {
        if (snapshot.Original is null)
        {
            return Session.PrepareEdit(snapshot.Reference, snapshot.Name);
        }

        var reference = _references.SingleOrDefault(candidate => candidate.Identity == snapshot.BaselineReference)
            ?? throw new ReplException("the immutable original's dependency graph is missing");
        var images = reference.Assets.Where(asset => asset.Kind is "managed" or "satellite")
            .Select(asset => _assets.TryGetValue(asset.Hash, out var retained)
            ? retained.Image : throw new ReplException("the immutable original's image is unavailable: " + asset.Name));
        var nativeLibraries = reference.Assets.Where(asset => asset.Kind == "native").ToDictionary(
            asset => Path.GetFileName(asset.Name), asset => asset.Path is { } path && File.Exists(path)
                && SessionCodec.Hash(File.ReadAllBytes(path)) == asset.Hash ? path
                    : throw new ReplException("the immutable original's native image is unavailable: " + asset.Name),
            StringComparer.OrdinalIgnoreCase);
        return Session.RestoreEditSnapshot(snapshot, images, nativeLibraries);
    }

    private static bool IsShared(Assembly assembly)
    {
        if (assembly == typeof(Session).Assembly || assembly == typeof(SessionDocument).Assembly)
        {
            return true;
        }

        if (OperatingSystem.IsBrowser())
        {
            var name = assembly.GetName().Name!;
            return name is "System.Private.CoreLib" or "netstandard" or "System" or "mscorlib"
                || name.StartsWith("System.", StringComparison.Ordinal);
        }

        return !assembly.IsDynamic && Path.GetDirectoryName(assembly.Location) == Path.GetDirectoryName(typeof(object).Assembly.Location);
    }
}
