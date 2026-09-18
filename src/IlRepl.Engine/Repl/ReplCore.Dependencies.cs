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
        static string Key(SessionReferenceAsset asset)
        {
            var name = new AssemblyName(asset.Name);
            return name.Name + "/" + name.CultureName;
        }

        var oldAssets = _references.Where(reference => reference.Origin != "baseline").SelectMany(reference => reference.Assets)
            .Where(asset => asset.Kind is "managed" or "satellite").DistinctBy(Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(Key, StringComparer.OrdinalIgnoreCase);
        var nextAssets = document.References.Where(reference => reference.Origin != "baseline").SelectMany(reference => reference.Assets)
            .Where(asset => asset.Kind is "managed" or "satellite").DistinctBy(Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(Key, StringComparer.OrdinalIgnoreCase);
        var changed = nextAssets.Values.Where(asset => !oldAssets.TryGetValue(Key(asset), out var previous)
            || previous.Hash != asset.Hash).ToArray();
        var removed = oldAssets.Values.Where(asset => !nextAssets.ContainsKey(Key(asset))).ToArray();
        var oldNames = oldAssets.Values.Select(asset => new AssemblyName(asset.Name).Name!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var replaced = changed.Concat(removed).Select(asset => new AssemblyName(asset.Name).Name!).Where(oldNames.Contains)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var oldNative = _references.Where(reference => reference.Origin != "baseline").SelectMany(reference => reference.Assets)
            .Where(asset => asset.Kind == "native").DistinctBy(asset => asset.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(asset => asset.Name, asset => asset.Hash, StringComparer.OrdinalIgnoreCase);
        var affectedReferences = document.References.Where(reference => reference.Origin != "baseline"
            && reference.Assets.Any(asset => asset.Kind == "native" && oldNative.TryGetValue(asset.Name, out var hash)
                && hash != asset.Hash)).Select(reference => reference.Identity).ToHashSet(StringComparer.Ordinal);
        var graphs = _references.Concat(document.References).Where(reference => reference.Origin != "baseline").ToArray();
        var previousCount = -1;
        while (previousCount != affectedReferences.Count + replaced.Count)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            previousCount = affectedReferences.Count + replaced.Count;
            affectedReferences.UnionWith(graphs.Where(reference => reference.Dependencies.Any(affectedReferences.Contains)
                || reference.Assets.Any(asset => asset.Kind is "managed" or "satellite"
                    && replaced.Contains(new AssemblyName(asset.Name).Name!))).Select(reference => reference.Identity));
            replaced.UnionWith(graphs.Where(reference => affectedReferences.Contains(reference.Identity))
                .SelectMany(reference => reference.Assets).Where(asset => asset.Kind is "managed" or "satellite")
                .Select(asset => new AssemblyName(asset.Name).Name!).Where(oldNames.Contains));
            // A retained image keeps the context that loaded it, including bindings below its direct dependencies.
            replaced.UnionWith(Session.Resolver.LoadedAssemblies.Where(assembly =>
                    assembly.GetReferencedAssemblies().Any(reference => replaced.Contains(reference.Name!)))
                .Select(assembly => assembly.GetName().Name!).Where(oldNames.Contains));
        }

        changed = [.. changed.Concat(nextAssets.Values.Where(asset => replaced.Contains(new AssemblyName(asset.Name).Name!)))
            .DistinctBy(Key, StringComparer.OrdinalIgnoreCase)];

        var users = ReferenceUsers(replaced).Distinct(StringComparer.Ordinal).ToArray();
        if (users.Length != 0)
        {
            throw new ReplException("changed dependencies are in use by " + string.Join(", ", users)
                + "; use .load <reference> --reload, or save and reopen in a fresh runtime");
        }

        using (var candidate = new ReplCore(new Session(), ColdOptions()))
        {
            var problems = candidate.WithCancellation(() => candidate.ReopenSession(document), _cancellationToken);
            if (problems.Length != 0)
            {
                throw new ReplException(string.Join(Environment.NewLine, problems));
            }
        }

        var images = document.Assets.ToDictionary(asset => asset.Hash, asset => asset.Image, StringComparer.Ordinal);
        Session.Resolver.ReplaceImages(changed.Select(asset => images[asset.Hash]), removed.Select(asset => new AssemblyName(asset.Name)));
        if (removed.Length != 0) Session.AdvanceGeneration();
        _references.Clear();
        _references.AddRange(document.References);
        foreach (var asset in document.Assets)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            _assets[asset.Hash] = asset;
        }

        _document = _document with { PackageLock = document.PackageLock };
        foreach (var entry in document.Entries.Where(entry => !_sourceEntries.Any(existing => existing.Identity == entry.Identity)))
        {
            _cancellationToken.ThrowIfCancellationRequested();
            _sourceEntries.Add(entry);
        }

        var visited = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in document.References.Where(reference => reference.Origin != "baseline"))
        {
            _cancellationToken.ThrowIfCancellationRequested();
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
            _cancellationToken.ThrowIfCancellationRequested();
            yield return "activated runtime binding " + reference;
        }

        bool Uses(Assembly assembly) => names.Contains(assembly.GetName().Name!)
            || assembly.GetReferencedAssemblies().Any(reference => names.Contains(reference.Name!));
        foreach (var method in Session.Methods.Where(method => Uses(method.Version.Definition.Assembly)))
        {
            _cancellationToken.ThrowIfCancellationRequested();
            yield return "method " + method.Signature.Name;
        }

        foreach (var type in Session.Types.Where(type => type.Definition is { } definition && Uses(definition.Assembly)))
        {
            _cancellationToken.ThrowIfCancellationRequested();
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
            _cancellationToken.ThrowIfCancellationRequested();
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
            _cancellationToken.ThrowIfCancellationRequested();
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
