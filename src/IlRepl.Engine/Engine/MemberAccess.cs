using System.Reflection;
using IlRepl.Engine.Binding;

namespace IlRepl.Engine;

/// <summary>
/// Where an access happens: the type whose body is being written, or null for the cell and for
/// session methods, and a short description for messages.
/// </summary>
/// <param name="Type">The prototype or runtime type of the body's owner, or null.</param>
/// <param name="Description">How messages name the accessor: <c>the cell</c>, <c>method Twice</c>, <c>class Line</c>.</param>
public sealed record AccessScope(Type? Type, string Description)
{
    /// <summary>
    /// The cell.
    /// </summary>
    public static AccessScope Cell { get; } = new(null, "the cell");
}

/// <summary>
/// The accessibility rules of ECMA-335 as the REPL teaches them, and the words ILAsm uses for
/// them. Because a consumer skips the runtime's checks for session assemblies, this is the only
/// place a session member's access is enforced.
/// </summary>
public static class MemberAccess
{
    /// <summary>
    /// The ILAsm access word for a field.
    /// </summary>
    /// <param name="attributes">The field attributes.</param>
    /// <returns>The word, for example <c>public</c> or <c>privatescope</c>.</returns>
    public static string AccessWord(FieldAttributes attributes) => (attributes & FieldAttributes.FieldAccessMask) switch
    {
        FieldAttributes.Public => "public",
        FieldAttributes.Private => "private",
        FieldAttributes.Family => "family",
        FieldAttributes.Assembly => "assembly",
        FieldAttributes.FamANDAssem => "famandassem",
        FieldAttributes.FamORAssem => "famorassem",
        _ => "privatescope",
    };

    /// <summary>
    /// The ILAsm access word for a method.
    /// </summary>
    /// <param name="attributes">The method attributes.</param>
    /// <returns>The word.</returns>
    public static string AccessWord(MethodAttributes attributes) => (attributes & MethodAttributes.MemberAccessMask) switch
    {
        MethodAttributes.Public => "public",
        MethodAttributes.Private => "private",
        MethodAttributes.Family => "family",
        MethodAttributes.Assembly => "assembly",
        MethodAttributes.FamANDAssem => "famandassem",
        MethodAttributes.FamORAssem => "famorassem",
        _ => "privatescope",
    };

    /// <summary>
    /// The ILAsm visibility words for a type.
    /// </summary>
    /// <param name="attributes">The type attributes.</param>
    /// <returns>The words, for example <c>public</c> or <c>nested family</c>.</returns>
    public static string VisibilityWord(TypeAttributes attributes) => (attributes & TypeAttributes.VisibilityMask) switch
    {
        TypeAttributes.Public => "public",
        TypeAttributes.NestedPublic => "nested public",
        TypeAttributes.NestedPrivate => "nested private",
        TypeAttributes.NestedFamily => "nested family",
        TypeAttributes.NestedAssembly => "nested assembly",
        TypeAttributes.NestedFamANDAssem => "nested famandassem",
        TypeAttributes.NestedFamORAssem => "nested famorassem",
        _ => "private",
    };

    /// <summary>
    /// Refuses a field access a session type does not allow. Framework and loaded fields are
    /// left to the runtime.
    /// </summary>
    /// <param name="field">The field.</param>
    /// <param name="scope">Where the access happens.</param>
    /// <param name="types">The table that knows the types being written.</param>
    /// <exception cref="ReplException">The access is not allowed.</exception>
    public static void CheckField(FieldInfo field, AccessScope scope, TypeTable types)
    {
        ArgumentNullException.ThrowIfNull(field);
        var problem = FieldVerdict(field, scope, types);
        if (problem is not null)
        {
            throw new ReplException(problem);
        }
    }

    /// <summary>
    /// Refuses a method access a session type does not allow.
    /// </summary>
    /// <param name="method">The resolved method.</param>
    /// <param name="scope">Where the access happens.</param>
    /// <param name="types">The table that knows the types being written.</param>
    /// <exception cref="ReplException">The access is not allowed.</exception>
    public static void CheckMethod(ResolvedMethod method, AccessScope scope, TypeTable types)
    {
        ArgumentNullException.ThrowIfNull(method);
        var problem = MethodVerdict(method, scope, types);
        if (problem is not null)
        {
            throw new ReplException(problem);
        }
    }

    /// <summary>
    /// Refuses a mention of a session type that is not visible from the scope.
    /// </summary>
    /// <param name="type">The type mentioned.</param>
    /// <param name="scope">Where the mention happens.</param>
    /// <param name="types">The table that knows the types being written.</param>
    /// <exception cref="ReplException">The type is not visible.</exception>
    public static void CheckType(Type type, AccessScope scope, TypeTable types)
    {
        ArgumentNullException.ThrowIfNull(type);
        var problem = TypeVerdict(type, scope, types);
        if (problem is not null)
        {
            throw new ReplException(problem);
        }
    }

    /// <summary>
    /// Decides a field access: null when allowed, otherwise the reason. Only session fields
    /// are judged unless <paramref name="judgeAll"/> is set. The receiver plays no part: the
    /// runtime checks only who accesses a family member, not through what, so neither does
    /// the REPL; the receiver rule of ECMA II.10.5.3 belongs to the verifier.
    /// </summary>
    /// <param name="field">The field.</param>
    /// <param name="scope">Where the access happens.</param>
    /// <param name="types">The table that knows the types being written.</param>
    /// <param name="judgeAll">True to judge fields of any assembly, as the equivalence harness does.</param>
    /// <returns>The reason the access is refused, or null.</returns>
    public static string? FieldVerdict(FieldInfo field, AccessScope scope, TypeTable types, bool judgeAll = false)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(types);
        var declaring = field.DeclaringType!;
        if (!judgeAll && !TypeRelations.IsSessionType(declaring))
        {
            return null;
        }

        var description = $"{TypeNameFormatter.Pretty(field.FieldType)} {TypeNameFormatter.Pretty(declaring)}::{field.Name}";
        var facts = Facts(types);
        var where = Context(scope);
        var declaringSymbol = RuntimeSymbolImporter.Import(declaring);
        return MemberEligibility.TypeVerdict(declaringSymbol, where, facts, judgeAll)
            ?? MemberEligibility.MemberVerdict(AccessWord(field.Attributes), declaringSymbol, description, where, facts);
    }

    /// <summary>
    /// Decides a method access: null when allowed, otherwise the reason.
    /// </summary>
    /// <param name="method">The resolved method.</param>
    /// <param name="scope">Where the access happens.</param>
    /// <param name="types">The table that knows the types being written.</param>
    /// <param name="judgeAll">True to judge methods of any assembly.</param>
    /// <returns>The reason the access is refused, or null.</returns>
    public static string? MethodVerdict(ResolvedMethod method, AccessScope scope, TypeTable types, bool judgeAll = false)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(types);
        if (method.Method is null || method.DeclaringType is null)
        {
            return null;
        }

        // The declaring type and the arguments of the instantiation are mentioned whatever
        // assembly the method belongs to; the member's own access is a session member's.
        var declaring = method.DeclaringType;
        if (TypeVerdict(declaring, scope, types, judgeAll) is { } declaringProblem)
        {
            return declaringProblem;
        }

        foreach (var argument in method.InstantiationArguments)
        {
            if (TypeVerdict(argument, scope, types, judgeAll) is { } argumentProblem)
            {
                return argumentProblem;
            }
        }

        if (!judgeAll && !TypeRelations.IsSessionType(declaring))
        {
            return null;
        }

        var attributes = method.Declared?.Attributes ?? method.Method.Attributes;
        var description = method.Declared is { } declared
            ? $"{declared.DescribeMember()} on {TypeNameFormatter.Pretty(declaring)}"
            : MemberResolver.Describe(method.Method);
        return MemberEligibility.MemberVerdict(AccessWord(attributes), RuntimeSymbolImporter.Import(declaring), description, Context(scope), Facts(types));
    }

    /// <summary>
    /// Decides whether a type is visible from a scope: null when it is, otherwise the reason.
    /// Element types and generic arguments are judged too.
    /// </summary>
    /// <param name="type">The type.</param>
    /// <param name="scope">Where the mention happens.</param>
    /// <param name="types">The table that knows the types being written.</param>
    /// <param name="judgeAll">True to judge types of any assembly.</param>
    /// <returns>The reason, or null.</returns>
    public static string? TypeVerdict(Type type, AccessScope scope, TypeTable types, bool judgeAll = false)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(types);
        return MemberEligibility.TypeVerdict(RuntimeSymbolImporter.Import(type), Context(scope), Facts(types), judgeAll);
    }

    /// <summary>
    /// The facts the symbol rules need, answered by reflection and the table of types being written.
    /// </summary>
    private static AccessFacts Facts(TypeTable types) => new(
        symbol => TypeRelations.BaseTypeOf(RuntimeBindingAdapter.Materialize(symbol), types) is { } baseType ? RuntimeSymbolImporter.Import(baseType) : null,
        symbol => TypeRelations.IsSessionType(RuntimeBindingAdapter.Materialize(symbol)),
        symbol => symbol is null ? "?" : TypeNameFormatter.Pretty(RuntimeBindingAdapter.Materialize(symbol)));

    private static AccessContext Context(AccessScope scope) => new(scope.Type is null ? null : RuntimeSymbolImporter.Import(scope.Type), scope.Description);
}
