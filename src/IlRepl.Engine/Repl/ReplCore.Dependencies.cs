using System.Reflection;
using IlRepl.Engine;
using IlRepl.Protocol;

namespace IlRepl.Repl;

/// <summary>
/// Publishes verified dependency graphs while protecting existing bound runtime identities.
/// </summary>
public sealed partial class ReplCore
{
    /// <summary>
    /// Describes whether a retained reference has usable verified implementation assets in this runtime.
    /// </summary>
    /// <param name="reference">The retained dependency manifest.</param>
    /// <returns>The user-facing availability or compatibility finding.</returns>
    internal string ReferenceStatus(SessionReference reference)
    {
        if (reference.Frameworks.FirstOrDefault(framework => framework != "Microsoft.NETCore.App") is { } required)
        {
            return "requires " + required;
        }

        if (reference.Assets.Length != 0 && reference.Assets.All(asset => asset.Kind == "reference"))
        {
            return "reference assemblies only; implementation required";
        }

        if (!Options.SupportsDependencyRestore && reference.Assets.Any(asset => asset.Kind == "native"))
        {
            return "native dependencies require desktop ilrepl";
        }

        var resolved = reference.Assets.Length != 0 || reference.Origin == "package" || HasLoadedReference(reference);
        return resolved && reference.Assets.All(asset => _assets.ContainsKey(asset.Hash))
            ? "available" : "missing assets; " + DependencyRecoveryHint();
    }

    /// <summary>
    /// Validates and publishes reference changes without replaying source or resetting unaffected runtime state.
    /// </summary>
    /// <param name="document">The staged dependency graph and matching source journal.</param>
    public void AdoptReferences(SessionDocument document)
    {
        SessionCodec.Validate(document);
        var oldAssets = _references.Where(reference => reference.Origin != "baseline").SelectMany(reference => reference.Assets)
            .Where(asset => asset.Kind is "managed" or "satellite").DistinctBy(asset => asset.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(asset => asset.Name, asset => asset.Hash,
                StringComparer.OrdinalIgnoreCase);
        var changed = document.References.Where(reference => reference.Origin != "baseline").SelectMany(reference => reference.Assets)
            .Where(asset => asset.Kind is "managed" or "satellite")
            .Where(asset => !oldAssets.TryGetValue(asset.Name, out var hash) || hash != asset.Hash)
            .DistinctBy(asset => asset.Hash).ToArray();
        var oldNames = oldAssets.Keys.Select(name => new AssemblyName(name).Name!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var replaced = changed.Select(asset => new AssemblyName(asset.Name).Name!).Where(oldNames.Contains)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var oldNative = _references.Where(reference => reference.Origin != "baseline").SelectMany(reference => reference.Assets)
            .Where(asset => asset.Kind == "native").DistinctBy(asset => asset.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(asset => asset.Name, asset => asset.Hash, StringComparer.OrdinalIgnoreCase);
        var nativeChanges = document.References.Where(reference => reference.Origin != "baseline"
            && reference.Assets.Any(asset => asset.Kind == "native" && oldNative.TryGetValue(asset.Name, out var hash)
                && hash != asset.Hash)).Select(reference => reference.Identity).ToHashSet(StringComparer.Ordinal);
        var previousCount = -1;
        while (previousCount != nativeChanges.Count)
        {
            previousCount = nativeChanges.Count;
            nativeChanges.UnionWith(document.References.Where(reference => reference.Origin != "baseline"
                && reference.Dependencies.Any(nativeChanges.Contains)).Select(reference => reference.Identity));
        }

        var nativeOwners = document.References.Where(reference => nativeChanges.Contains(reference.Identity))
            .SelectMany(reference => reference.Assets).Where(asset => asset.Kind is "managed" or "satellite").ToArray();
        replaced.UnionWith(nativeOwners.Select(asset => new AssemblyName(asset.Name).Name!).Where(oldNames.Contains));
        changed = [.. changed.Concat(nativeOwners).DistinctBy(asset => asset.Name, StringComparer.OrdinalIgnoreCase)];

        var users = ReferenceUsers(replaced).Distinct(StringComparer.Ordinal).ToArray();
        if (users.Length != 0)
        {
            throw new ReplException("changed dependencies are in use by " + string.Join(", ", users)
                + "; use .load <reference> --reload, or save and reopen in a fresh runtime");
        }

        using (var candidate = new ReplCore(new Session(), ColdOptions()))
        {
            var problems = candidate.ReopenSession(document);
            if (problems.Length != 0)
            {
                throw new ReplException(string.Join(Environment.NewLine, problems));
            }
        }

        var images = document.Assets.ToDictionary(asset => asset.Hash, asset => asset.Image, StringComparer.Ordinal);
        Session.Resolver.ReplaceImages(changed.Select(asset => images[asset.Hash]));
        _references.Clear();
        _references.AddRange(document.References);
        foreach (var asset in document.Assets)
        {
            _assets[asset.Hash] = asset;
        }

        _document = _document with { PackageLock = document.PackageLock };
        foreach (var entry in document.Entries.Where(entry => !_sourceEntries.Any(existing => existing.Identity == entry.Identity)))
        {
            _sourceEntries.Add(entry);
        }

        var visited = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in document.References.Where(reference => reference.Origin != "baseline"))
        {
            RestoreReference(reference, visited);
        }
    }

    private ReplOptions ColdOptions() => new() { EchoStack = false, SupportsDependencyRestore = Options.SupportsDependencyRestore };

    private bool HasLoadedReference(SessionReference reference)
    {
        if (reference.Origin != "assembly") return false;
        try
        {
            var name = new AssemblyName(reference.Request).Name;
            return Session.Resolver.LoadedAssemblies.Any(assembly =>
                string.Equals(assembly.GetName().Name, name, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception exception) when (exception is FileLoadException or ArgumentException)
        {
            return false;
        }
    }

    private string DependencyRecoveryHint(bool changed = false) => Options.SupportsDependencyRestore
        ? changed ? "use .load to adopt its new contents" : "use .session restore"
        : (changed ? "reload" : "restore")
            + " dependencies in desktop ilrepl, save with .session save --embed, then open that file in this demo";

    private IEnumerable<string> ReferenceUsers(HashSet<string> names)
    {
        foreach (var reference in Session.ActivatedReferences.Where(names.Contains))
        {
            yield return "activated runtime binding " + reference;
        }

        bool Uses(Assembly assembly) => names.Contains(assembly.GetName().Name!)
            || assembly.GetReferencedAssemblies().Any(reference => names.Contains(reference.Name!));
        foreach (var method in Session.Methods.Where(method => Uses(method.Version.Definition.Assembly)))
        {
            yield return "method " + method.Signature.Name;
        }

        foreach (var type in Session.Types.Where(type => type.Definition is { } definition && Uses(definition.Assembly)))
        {
            yield return "type " + type.Declaration.Name;
        }

        foreach (var edit in Session.Edits.Where(edit => edit.Dependencies.Any(dependency =>
            names.Contains(new AssemblyName(dependency.Assembly).Name!))))
        {
            yield return "edit " + edit.Name;
        }

        bool UsesType(Type type) => Uses(type.Assembly) || (type.HasElementType && UsesType(type.GetElementType()!))
            || (type.IsGenericType && type.GetGenericArguments().Any(UsesType));
        foreach (var state in new[] { Session.Cell, Session.State }.Distinct())
        {
            if (state.Arguments.Any(argument => UsesType(argument.Type)) || state.Locals.Any(local => UsesType(local.Type))
                || state.Entries.Any(entry => entry.Instruction?.Operand switch
                {
                    Type type => UsesType(type),
                    MemberInfo member => Uses(member.Module.Assembly),
                    ResolvedMethod { Method: { } method } => Uses(method.Module.Assembly),
                    _ => entry.CatchType is { } caught && UsesType(caught),
                }))
            {
                yield return "current cell or arguments";
            }
        }

        if (Session.TypeArguments?.Any(UsesType) == true)
        {
            yield return "generic bindings";
        }
    }

    private void PrepareReferenceImages(SessionDocument document, List<string> problems)
    {
        var images = document.Assets.ToDictionary(asset => asset.Hash, asset => asset.Image, StringComparer.Ordinal);
        foreach (var reference in document.References.Where(reference => reference.Origin != "baseline"))
        {
            foreach (var asset in reference.Assets.Where(asset => asset.Kind is "managed" or "satellite"))
            {
                if (!images.TryGetValue(asset.Hash, out var image))
                {
                    continue;
                }

                try
                {
                    Session.Resolver.RegisterImages([image]);
                }
                catch (Exception exception) when (exception is ReplException or BadImageFormatException or IOException)
                {
                    problems.Add(reference.Request + ": " + exception.Message);
                }
            }
        }
    }
}
