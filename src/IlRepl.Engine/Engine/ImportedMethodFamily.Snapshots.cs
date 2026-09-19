using System.Reflection;
using IlRepl.Protocol;

namespace IlRepl.Engine;

/// <summary>
/// Restores source-time method bindings from an immutable assembly graph independently of live dependency replacements.
/// </summary>
internal sealed partial class ImportedMethodFamily
{
    private Session? _liveSession;
    private Dictionary<string, string> _signatureHeaders = new(StringComparer.Ordinal);

    /// <summary>
    /// The dependency resolver that owns the exact external assemblies bound by this family.
    /// </summary>
    internal TypeResolver SourceResolver => _session.Resolver;

    /// <summary>
    /// Records the original metadata identity and every source-time binding needed to parse later revisions faithfully.
    /// </summary>
    /// <param name="capture">Retains an assembly before its identity is written.</param>
    /// <returns>The immutable original and its parsing bindings.</returns>
    internal SessionEditSnapshot CaptureSnapshot(Action<Assembly, TypeResolver> capture)
    {
        string TypeName(Type type)
        {
            CaptureType(type);
            return type.AssemblyQualifiedName ?? throw new ReplException("a captured type has no stable assembly-qualified name");
        }

        void CaptureType(Type type)
        {
            if (type.IsGenericParameter)
            {
                return;
            }

            if (type.HasElementType)
            {
                CaptureType(type.GetElementType()!);
                return;
            }

            capture(type.Assembly, _session.Resolver);
            foreach (var argument in type.GenericTypeArguments)
            {
                CaptureType(argument);
            }
        }

        SessionMethodIdentity Identity(MethodBase method)
        {
            capture(method.Module.Assembly, _session.Resolver);
            return new SessionMethodIdentity
            {
                Assembly = method.Module.Assembly.FullName!,
                Module = method.Module.ModuleVersionId.ToString(),
                Token = method.MetadataToken,
                TypeArguments = method.DeclaringType!.GetGenericArguments()
                    .Select(type => type.IsGenericParameter ? null : TypeName(type)).ToArray(),
                MethodArguments = method.GetGenericArguments()
                    .Select(type => type.IsGenericParameter ? null : TypeName(type)).ToArray(),
            };
        }

        return new SessionEditSnapshot
        {
            Original = Identity(Selected.Listing.Requested),
            PinnedMethods = _pinned.ToDictionary(pair => pair.Key, pair => Identity(pair.Value), StringComparer.Ordinal),
            SignatureHeaders = new Dictionary<string, string>(_signatureHeaders, StringComparer.Ordinal),
            TypeAliases = _sourceTypes.Entries.ToDictionary(pair => pair.FullName, pair => TypeName(pair.Type), StringComparer.Ordinal),
            MethodAliases = _sourceTypes.MethodAliases.ToDictionary(pair => pair.Key, pair => Identity(pair.Value), StringComparer.Ordinal),
        };
    }

    /// <summary>
    /// Reconstructs the original family using captured metadata and source without activating user code.
    /// </summary>
    /// <param name="snapshot">The original source and binding identities.</param>
    /// <param name="owner">The session receiving the edit.</param>
    /// <param name="resolver">The isolated resolver for immutable original dependencies.</param>
    /// <returns>The captured original family with source-time helper and method bindings.</returns>
    internal static ImportedMethodFamily RestoreSnapshot(SessionEditSnapshot snapshot, Session owner, TypeResolver resolver)
    {
        Assembly Assembly(AssemblyName name) => resolver.Assemblies.FirstOrDefault(assembly => assembly.GetName().FullName == name.FullName)
            ?? resolver.Load(name.FullName);

        Type ResolveType(string name) => Type.GetType(name, Assembly, (assembly, type, ignoreCase) =>
            assembly?.GetType(type, throwOnError: false, ignoreCase), throwOnError: true)!;

        MethodBase Method(SessionMethodIdentity identity)
        {
            var assembly = Assembly(new AssemblyName(identity.Assembly));
            var module = assembly.GetModules().SingleOrDefault(candidate => candidate.ModuleVersionId.ToString() == identity.Module)
                ?? throw new ReplException("the immutable edit original's module is unavailable: " + identity.Assembly);
            var method = module.ResolveMethod(identity.Token) ?? throw new ReplException("the captured original method is missing");
            var declaring = method.DeclaringType!;
            if (identity.TypeArguments.Length != declaring.GetGenericArguments().Length
                || identity.MethodArguments.Length != method.GetGenericArguments().Length)
            {
                throw new ReplException("the captured original's generic arity does not match its metadata");
            }

            if (identity.TypeArguments.Any(argument => argument is not null))
            {
                declaring = declaring.MakeGenericType(identity.TypeArguments.Select((argument, index) =>
                    argument is null ? declaring.GetGenericArguments()[index] : ResolveType(argument)).ToArray());
                method = MethodBase.GetMethodFromHandle(method.MethodHandle, declaring.TypeHandle)
                    ?? throw new ReplException("the captured declaring-type instantiation has no matching method");
            }

            if (identity.MethodArguments.Any(argument => argument is not null))
            {
                var generic = (MethodInfo)method;
                method = generic.MakeGenericMethod(identity.MethodArguments.Select((argument, index) =>
                    argument is null ? generic.GetGenericArguments()[index] : ResolveType(argument)).ToArray());
            }

            return method;
        }

        var source = new Session(resolver) { DeferActivation = true };
        foreach (var (name, identity) in snapshot.TypeAliases)
        {
            source.TypeTable.Add(name, ResolveType(identity));
        }

        foreach (var (name, identity) in snapshot.MethodAliases)
        {
            source.TypeTable.MethodAliases.Add(name, Method(identity));
        }

        var pinned = snapshot.PinnedMethods.ToDictionary(pair => pair.Key, pair => (MethodInfo)Method(pair.Value), StringComparer.Ordinal);
        var signatures = snapshot.SignatureHeaders.Select(pair => MethodHeaderParser.Parse(
            pair.Value.TrimStart()[".method".Length..].TrimStart(), source.InspectionContext, out _)).ToArray();
        var requested = Method(snapshot.Original ?? throw new ReplException("the immutable original method identity is missing"));
        using var references = resolver.EnterContext();
        var listing = MethodDisassembler.Disassemble(requested, source);
        var body = MethodEditBody.Parse(listing, string.Join('\n', snapshot.Source), source, signatures);
        return new ImportedMethodFamily(snapshot.Name, source, signatures, pinned, body, source.TypeTable.Clone())
        {
            _liveSession = owner,
            _signatureHeaders = new Dictionary<string, string>(snapshot.SignatureHeaders, StringComparer.Ordinal),
        };
    }

    private void RefreshSnapshotSession()
    {
        if (_liveSession is null)
        {
            return;
        }

        _session.Resolver.AddSnapshotReferences(_liveSession.Resolver);
        _session.DeferActivation = _liveSession.DeferActivation;
        _session.ReplaceSnapshotTypes(_liveSession.TypeTable);
        foreach (var type in SourceTypes)
        {
            _session.TypeTable.Add(type.FullName!.Replace('+', '/'), type);
        }
    }
}
