using System.Reflection;

namespace IlRepl.Engine;

/// <summary>
/// Maps the identities a body was bound to onto the identities it is emitted against. A cell
/// binds session methods to their trampolines; an export binds them to the methods of the
/// persisted <c>IlRepl.Cell</c>; a family under construction maps its prototype builders onto
/// real definitions. Constructed types (arrays of any rank, byrefs, pointers, generic
/// instantiations) are rebuilt around their mapped parts, so a mapping of <c>Point</c> also maps
/// <c>Point[]</c>, <c>Point&amp;</c>, and <c>List&lt;Point&gt;</c>.
/// </summary>
public sealed class EmitMap
{
    private readonly Dictionary<Type, Type> _types = [];
    private readonly Dictionary<FieldInfo, FieldInfo> _fields = [];
    private readonly Dictionary<MethodBase, MethodBase> _methods = [];
    private readonly Func<MethodSignature, MethodInfo> _sessionMethods;

    /// <summary>
    /// Initializes a map that binds session methods through the given lookup.
    /// </summary>
    /// <param name="sessionMethods">Returns the method a session-method reference binds to.</param>
    public EmitMap(Func<MethodSignature, MethodInfo> sessionMethods)
    {
        ArgumentNullException.ThrowIfNull(sessionMethods);
        _sessionMethods = sessionMethods;
    }

    /// <summary>
    /// A map with no entries whose session methods resolve through a name table.
    /// </summary>
    /// <param name="methods">The session methods by name.</param>
    /// <returns>The map.</returns>
    public static EmitMap ForSessionMethods(IReadOnlyDictionary<string, MethodInfo> methods)
    {
        ArgumentNullException.ThrowIfNull(methods);
        return new EmitMap(signature => methods.TryGetValue(signature.Name, out var method)
            ? method
            : throw new ReplException($"no method '{signature.Name}' is bound in the session"));
    }

    /// <summary>
    /// Records that <paramref name="from"/> is emitted as <paramref name="to"/>.
    /// </summary>
    /// <param name="from">The bound type.</param>
    /// <param name="to">The type to emit.</param>
    public void Add(Type from, Type to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        _types[from] = to;
    }

    /// <summary>
    /// Records that <paramref name="from"/> is emitted as <paramref name="to"/>.
    /// </summary>
    /// <param name="from">The bound field.</param>
    /// <param name="to">The field to emit.</param>
    public void Add(FieldInfo from, FieldInfo to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        _fields[from] = to;
    }

    /// <summary>
    /// Records that <paramref name="from"/> is emitted as <paramref name="to"/>.
    /// </summary>
    /// <param name="from">The bound method or constructor.</param>
    /// <param name="to">The method or constructor to emit.</param>
    public void Add(MethodBase from, MethodBase to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        _methods[from] = to;
    }

    /// <summary>
    /// Maps a type, rebuilding constructed types around their mapped parts.
    /// </summary>
    /// <param name="type">The bound type.</param>
    /// <returns>The type to emit.</returns>
    public Type Map(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (_types.TryGetValue(type, out var mapped))
        {
            return mapped;
        }

        if (type.IsByRef)
        {
            return Rebuild(type, Map(type.GetElementType()!).MakeByRefType());
        }

        if (type.IsPointer)
        {
            return Rebuild(type, Map(type.GetElementType()!).MakePointerType());
        }

        if (type.IsArray)
        {
            var element = Map(type.GetElementType()!);
            return Rebuild(type, type.IsSZArray ? element.MakeArrayType() : element.MakeArrayType(type.GetArrayRank()));
        }

        if (type.IsConstructedGenericType)
        {
            var arguments = type.GetGenericArguments().Select(Map).ToArray();
            return Rebuild(type, Map(type.GetGenericTypeDefinition()).MakeGenericType(arguments));
        }

        return type;
    }

    /// <summary>
    /// Maps a field.
    /// </summary>
    /// <param name="field">The bound field.</param>
    /// <returns>The field to emit.</returns>
    public FieldInfo Map(FieldInfo field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return _fields.TryGetValue(field, out var mapped) ? mapped : field;
    }

    /// <summary>
    /// Maps a method or constructor.
    /// </summary>
    /// <param name="method">The bound method.</param>
    /// <returns>The method to emit.</returns>
    public MethodBase Map(MethodBase method)
    {
        ArgumentNullException.ThrowIfNull(method);
        return _methods.TryGetValue(method, out var mapped) ? mapped : method;
    }

    /// <summary>
    /// The method a session-method reference binds to.
    /// </summary>
    /// <param name="signature">The session method's signature.</param>
    /// <returns>The method to emit.</returns>
    public MethodInfo SessionMethod(MethodSignature signature)
    {
        ArgumentNullException.ThrowIfNull(signature);
        return _sessionMethods(signature);
    }

    private static Type Rebuild(Type original, Type rebuilt) => rebuilt == original ? original : rebuilt;
}
