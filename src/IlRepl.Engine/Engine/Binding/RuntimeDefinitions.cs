using System.Reflection;
using System.Runtime.CompilerServices;

namespace IlRepl.Engine.Binding;

/// <summary>
/// Gives every runtime definition one <see cref="DefinitionId"/> and finds the definition again
/// from its id while it is alive. A loaded definition is keyed by its assembly instance, module,
/// and token, so the metadata reader and reflection agree on it without meeting; a builder is
/// keyed by the object itself. The tables hold their runtime objects weakly, so nothing here keeps
/// a collectible session assembly alive.
/// </summary>
public static class RuntimeDefinitions
{
    private static readonly Lock Gate = new();
    private static readonly ConditionalWeakTable<Assembly, StrongBox<long>> AssemblyIds = [];
    private static readonly ConditionalWeakTable<object, StrongBox<DefinitionId>> DeclarationIds = [];
    private static readonly Dictionary<DefinitionId, WeakReference<Type>> Types = [];
    private static readonly Dictionary<DefinitionId, WeakReference<MethodBase>> Methods = [];
    private static readonly Dictionary<DefinitionId, WeakReference<FieldInfo>> Fields = [];
    private static readonly Dictionary<(DefinitionId Owner, bool IsMethod, int Position), WeakReference<Type>> Parameters = [];
    private static long s_nextAssembly;
    private static long s_nextDeclaration;
    private static int s_registrations;

    /// <summary>
    /// The instance number of a loaded or dynamic assembly. Two loads of the same bytes are two numbers.
    /// </summary>
    /// <param name="assembly">The assembly.</param>
    /// <returns>The number, never 0.</returns>
    public static long AssemblyInstance(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        return AssemblyIds.GetValue(assembly, _ => new StrongBox<long>(Interlocked.Increment(ref s_nextAssembly))).Value;
    }

    /// <summary>
    /// True when a type belongs to a dynamic assembly: a builder, a placeholder, or a type emitted
    /// for a cell, none of which has a token the metadata reader could see.
    /// </summary>
    /// <param name="type">The type.</param>
    /// <returns>True for a dynamic type.</returns>
    public static bool IsDynamic(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        try
        {
            return type.Assembly.IsDynamic;
        }
        catch (NotSupportedException)
        {
            return true;
        }
    }

    /// <summary>
    /// The identity of a type definition. Constructed types, element types, and generic parameters have none.
    /// </summary>
    /// <param name="definition">The definition.</param>
    /// <returns>The identity.</returns>
    public static DefinitionId Of(Type definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.IsGenericParameter || definition.HasElementType || (definition.IsGenericType && !definition.IsGenericTypeDefinition))
        {
            throw new ArgumentException($"'{definition}' is not a type definition", nameof(definition));
        }

        DefinitionId id;
        if (IsDynamic(definition))
        {
            id = DeclarationIds.GetValue(definition, t => new StrongBox<DefinitionId>(DefinitionId.ForDeclaration(AssemblyInstance(((Type)t).Assembly), Interlocked.Increment(ref s_nextDeclaration)))).Value;
        }
        else
        {
            id = DefinitionId.Loaded(AssemblyInstance(definition.Assembly), definition.Module.ModuleVersionId, definition.MetadataToken);
        }

        Remember(Types, id, definition);
        return id;
    }

    /// <summary>
    /// The identity of a method or constructor definition. A method on a constructed type and an
    /// instantiated generic method share the identity of their definition.
    /// </summary>
    /// <param name="method">The method.</param>
    /// <returns>The identity.</returns>
    public static DefinitionId Of(MethodBase method)
    {
        ArgumentNullException.ThrowIfNull(method);
        DefinitionId id;
        if (method.Module.Assembly.IsDynamic)
        {
            id = DeclarationIds.GetValue(method, m => new StrongBox<DefinitionId>(DefinitionId.ForDeclaration(AssemblyInstance(((MethodBase)m).Module.Assembly), Interlocked.Increment(ref s_nextDeclaration)))).Value;
            Remember(Methods, id, method);
        }
        else
        {
            id = DefinitionId.Loaded(AssemblyInstance(method.Module.Assembly), method.Module.ModuleVersionId, method.MetadataToken);
            if (method is not MethodInfo { IsGenericMethod: true, IsGenericMethodDefinition: false } && (method.DeclaringType is null || !method.DeclaringType.IsGenericType || method.DeclaringType.IsGenericTypeDefinition))
            {
                Remember(Methods, id, method);
            }
        }

        return id;
    }

    /// <summary>
    /// The identity of a field definition. A field on a constructed type shares the identity of its definition.
    /// </summary>
    /// <param name="field">The field.</param>
    /// <returns>The identity.</returns>
    public static DefinitionId Of(FieldInfo field)
    {
        ArgumentNullException.ThrowIfNull(field);
        DefinitionId id;
        if (field.Module.Assembly.IsDynamic)
        {
            id = DeclarationIds.GetValue(field, f => new StrongBox<DefinitionId>(DefinitionId.ForDeclaration(AssemblyInstance(((FieldInfo)f).Module.Assembly), Interlocked.Increment(ref s_nextDeclaration)))).Value;
            Remember(Fields, id, field);
        }
        else
        {
            id = DefinitionId.Loaded(AssemblyInstance(field.Module.Assembly), field.Module.ModuleVersionId, field.MetadataToken);
            if (field.DeclaringType is null || !field.DeclaringType.IsGenericType || field.DeclaringType.IsGenericTypeDefinition)
            {
                Remember(Fields, id, field);
            }
        }

        return id;
    }

    /// <summary>
    /// A fresh identity for a declaration no runtime object stands for yet, such as a session
    /// method's signature.
    /// </summary>
    /// <param name="declaration">The declaration object, which keeps the identity while it lives.</param>
    /// <param name="assembly">The assembly instance the declaration belongs to, or 0 for the session itself.</param>
    /// <returns>The identity.</returns>
    public static DefinitionId OfDeclaration(object declaration, long assembly)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        return DeclarationIds.GetValue(declaration, _ => new StrongBox<DefinitionId>(DefinitionId.ForDeclaration(assembly, Interlocked.Increment(ref s_nextDeclaration)))).Value;
    }

    /// <summary>
    /// Remembers the runtime type of a generic parameter, so a parameter symbol finds it again.
    /// </summary>
    /// <param name="owner">The owner's identity.</param>
    /// <param name="isMethod">True for a method parameter.</param>
    /// <param name="position">The position.</param>
    /// <param name="parameter">The runtime type.</param>
    public static void RememberParameter(DefinitionId owner, bool isMethod, int position, Type parameter)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        lock (Gate)
        {
            Parameters[(owner, isMethod, position)] = new WeakReference<Type>(parameter);
            Prune();
        }
    }

    /// <summary>
    /// The runtime type of a definition, while it is alive.
    /// </summary>
    /// <param name="id">The identity.</param>
    /// <returns>The type, or null.</returns>
    public static Type? TypeOf(DefinitionId id) => Recall(Types, id);

    /// <summary>
    /// The runtime method of a definition, while it is alive.
    /// </summary>
    /// <param name="id">The identity.</param>
    /// <returns>The method, or null.</returns>
    public static MethodBase? MethodOf(DefinitionId id) => Recall(Methods, id);

    /// <summary>
    /// The runtime field of a definition, while it is alive.
    /// </summary>
    /// <param name="id">The identity.</param>
    /// <returns>The field, or null.</returns>
    public static FieldInfo? FieldOf(DefinitionId id) => Recall(Fields, id);

    /// <summary>
    /// The runtime type of a generic parameter, while its owner is alive.
    /// </summary>
    /// <param name="owner">The owner's identity.</param>
    /// <param name="isMethod">True for a method parameter.</param>
    /// <param name="position">The position.</param>
    /// <returns>The type, or null.</returns>
    public static Type? ParameterOf(DefinitionId owner, bool isMethod, int position)
    {
        lock (Gate)
        {
            return Parameters.TryGetValue((owner, isMethod, position), out var weak) && weak.TryGetTarget(out var type) ? type : null;
        }
    }

    private static void Remember<T>(Dictionary<DefinitionId, WeakReference<T>> table, DefinitionId id, T value) where T : class
    {
        lock (Gate)
        {
            if (!table.TryGetValue(id, out var existing) || !existing.TryGetTarget(out _))
            {
                table[id] = new WeakReference<T>(value);
                Prune();
            }
        }
    }

    private static T? Recall<T>(Dictionary<DefinitionId, WeakReference<T>> table, DefinitionId id) where T : class
    {
        lock (Gate)
        {
            return table.TryGetValue(id, out var weak) && weak.TryGetTarget(out var value) ? value : null;
        }
    }

    private static void Prune()
    {
        // Dead entries are swept every so many registrations; the tables never grow without bound.
        if (++s_registrations % 4096 != 0)
        {
            return;
        }

        Sweep(Types);
        Sweep(Methods);
        Sweep(Fields);
        foreach (var key in Parameters.Where(p => !p.Value.TryGetTarget(out _)).Select(p => p.Key).ToList())
        {
            Parameters.Remove(key);
        }
    }

    private static void Sweep<T>(Dictionary<DefinitionId, WeakReference<T>> table) where T : class
    {
        foreach (var key in table.Where(p => !p.Value.TryGetTarget(out _)).Select(p => p.Key).ToList())
        {
            table.Remove(key);
        }
    }
}
