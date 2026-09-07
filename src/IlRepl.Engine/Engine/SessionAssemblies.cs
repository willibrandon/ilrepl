using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace IlRepl.Engine;

/// <summary>
/// Names, loads, and recognizes the assemblies a session owns. Each gets a globally unique simple
/// name <c>ilrepl.&lt;kind&gt;.&lt;N&gt;</c> and the fixed version 1.0.0.0, is loaded into a
/// <see cref="DefinitionLoadContext"/> of its own, and is registered by the identity of the loaded
/// assembly object. Recognition is a lookup, never a name comparison, so an unrelated assembly
/// that happens to be called <c>ilrepl.types.1</c> is not a session assembly.
/// </summary>
public static class SessionAssemblies
{
    /// <summary>
    /// The version every session assembly carries. Version components are two bytes wide, so the
    /// counter lives in the simple name instead.
    /// </summary>
    public static readonly Version Version = new(1, 0, 0, 0);

    private const string Prefix = "ilrepl.";
    private static readonly ConditionalWeakTable<Assembly, DefinitionAssembly> Owners = [];
    private static readonly Dictionary<string, WeakReference<Assembly>> Index = new(StringComparer.Ordinal);
    private static readonly Lock IndexLock = new();
    private static int s_counter;
    private static bool s_browserResolving;

    /// <summary>
    /// Allocates the next unique simple name for an assembly of the given kind.
    /// </summary>
    /// <param name="kind">What the assembly will hold.</param>
    /// <returns>A name such as <c>ilrepl.types.7</c>.</returns>
    public static string NextName(SessionAssemblyKind kind)
    {
        var word = kind switch
        {
            SessionAssemblyKind.Types => "types",
            SessionAssemblyKind.Methods => "methods",
            SessionAssemblyKind.Trampoline => "tramp",
            _ => "cell",
        };
        return Prefix + word + "." + Interlocked.Increment(ref s_counter).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The assembly name for a session assembly: the unique simple name and the fixed version.
    /// </summary>
    /// <param name="name">A name from <see cref="NextName"/>.</param>
    /// <returns>The assembly name.</returns>
    public static AssemblyName MakeAssemblyName(string name) => new(name) { Version = Version };

    /// <summary>
    /// True when a simple name has the shape of a session assembly name. Only used to decide
    /// whether a load request should consult the registry; recognition is by identity.
    /// </summary>
    /// <param name="simpleName">The simple name.</param>
    /// <returns>True for <c>ilrepl.*</c>.</returns>
    public static bool IsSessionName(string? simpleName) => simpleName is not null && simpleName.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>
    /// Creates the load context for a cell built with Reflection.Emit. On CoreCLR the cell is
    /// defined under contextual reflection so its dynamic assembly lands in a collectible context
    /// that resolves session names; the browser runtime ignores contextual reflection for dynamic
    /// assemblies, so there the default context resolves session names instead (every context is
    /// non-collectible on that runtime, which is what makes that legal).
    /// </summary>
    /// <param name="name">The cell's name.</param>
    /// <returns>The context, or null on the browser.</returns>
    public static DefinitionLoadContext? CreateCellContext(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (OperatingSystem.IsBrowser())
        {
            EnsureDefaultContextResolves();
            return null;
        }

        return new DefinitionLoadContext(name);
    }

    /// <summary>
    /// Loads a session assembly from its image into a context of its own and registers it.
    /// </summary>
    /// <param name="image">The PE image.</param>
    /// <param name="name">The simple name the image was written with.</param>
    /// <param name="kind">What the assembly holds.</param>
    /// <param name="dependencies">The session assemblies the image references.</param>
    /// <returns>The registered assembly.</returns>
    public static DefinitionAssembly Load(byte[] image, string name, SessionAssemblyKind kind, IReadOnlyList<DefinitionAssembly> dependencies)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(dependencies);
        var context = new DefinitionLoadContext(name);
        var assembly = context.LoadFromStream(new MemoryStream(image, writable: false));
        var definition = new DefinitionAssembly(assembly, kind, [.. dependencies], context);
        Register(assembly, definition);
        return definition;
    }

    /// <summary>
    /// Registers a cell built with Reflection.Emit. Both the builder and the runtime assembly of
    /// the created type are registered, because they are different objects on CoreCLR.
    /// </summary>
    /// <param name="builder">The assembly builder.</param>
    /// <param name="created">A type created in it.</param>
    /// <param name="dependencies">The session assemblies the cell references.</param>
    /// <param name="context">The context the cell was defined in, or null on the browser, where dynamic assemblies always land in the default context.</param>
    /// <returns>The registered assembly.</returns>
    public static DefinitionAssembly RegisterCell(AssemblyBuilder builder, Type created, IReadOnlyList<DefinitionAssembly> dependencies, DefinitionLoadContext? context)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(created);
        ArgumentNullException.ThrowIfNull(dependencies);
        var definition = new DefinitionAssembly(created.Assembly, SessionAssemblyKind.Cell, [.. dependencies], context);
        Register(created.Assembly, definition);
        if (!ReferenceEquals(created.Assembly, builder))
        {
            Owners.AddOrUpdate(builder, definition);
        }

        return definition;
    }

    /// <summary>
    /// Initiates unloading of a definition the session no longer holds. Objects that still
    /// reference the assembly keep it, and its dependencies, alive until they are released.
    /// </summary>
    /// <param name="definition">The definition.</param>
    public static void Release(DefinitionAssembly definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.Context is { IsCollectible: true } context)
        {
            context.Unload();
        }
    }

    /// <summary>
    /// True when the assembly was loaded or built by a session.
    /// </summary>
    /// <param name="assembly">The assembly.</param>
    /// <returns>True for a registered session assembly.</returns>
    public static bool IsSessionAssembly(Assembly? assembly) => assembly is not null && Owners.TryGetValue(assembly, out _);

    /// <summary>
    /// Finds the record of a session assembly.
    /// </summary>
    /// <param name="assembly">The assembly.</param>
    /// <param name="definition">The record.</param>
    /// <returns>True when the assembly is a session assembly.</returns>
    public static bool TryGetDefinition(Assembly assembly, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out DefinitionAssembly? definition)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        return Owners.TryGetValue(assembly, out definition);
    }

    /// <summary>
    /// True when a type was defined by a session: its definition, for a constructed generic type.
    /// Arrays, pointers, and byrefs answer false; their own assembly is their element's.
    /// </summary>
    /// <param name="type">The type.</param>
    /// <returns>True for a session type.</returns>
    public static bool IsSessionType(Type? type)
    {
        if (type is null || type.HasElementType || type.IsGenericParameter)
        {
            return false;
        }

        if (type.IsConstructedGenericType)
        {
            type = type.GetGenericTypeDefinition();
        }

        return IsSessionAssembly(type.Assembly);
    }

    /// <summary>
    /// True when a value is an instance of a session type. An array of session values is not.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <returns>True for a session instance.</returns>
    public static bool IsSessionInstance(object? value) => value is not null && IsSessionType(value.GetType());

    /// <summary>
    /// Resolves a session assembly by its full name. Entries are weak, so a definition that
    /// nothing references any more is simply absent.
    /// </summary>
    /// <param name="name">The assembly name.</param>
    /// <returns>The assembly, or null.</returns>
    internal static Assembly? Resolve(AssemblyName name)
    {
        lock (IndexLock)
        {
            if (Index.TryGetValue(name.FullName, out var weak))
            {
                if (weak.TryGetTarget(out var assembly))
                {
                    return assembly;
                }

                Index.Remove(name.FullName);
            }

            return null;
        }
    }

    private static void EnsureDefaultContextResolves()
    {
        lock (IndexLock)
        {
            if (s_browserResolving)
            {
                return;
            }

            s_browserResolving = true;
            System.Runtime.Loader.AssemblyLoadContext.Default.Resolving += (_, name) => IsSessionName(name.Name) ? Resolve(name) : null;
        }
    }

    private static void Register(Assembly assembly, DefinitionAssembly definition)
    {
        Owners.AddOrUpdate(assembly, definition);
        lock (IndexLock)
        {
            Index[assembly.FullName!] = new WeakReference<Assembly>(assembly);
        }
    }
}
