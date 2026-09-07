namespace IlRepl.Engine;

/// <summary>
/// The name, return type, and parameters of a method defined with <c>.method</c>. Calls resolve
/// against it before the method exists as a builder, so nothing here reflects over emitted code.
/// </summary>
/// <param name="Name">The method name.</param>
/// <param name="ReturnType">The return type; <c>void</c> when the method returns nothing.</param>
/// <param name="Parameters">The parameters in order. Names are optional and values are unused.</param>
public sealed record MethodSignature(string Name, Type ReturnType, IReadOnlyList<ArgumentDeclaration> Parameters)
{
    /// <summary>
    /// The method attributes as declared: access, <c>static</c>, <c>virtual</c>, and the rest. A
    /// session method is public and static.
    /// </summary>
    public System.Reflection.MethodAttributes Attributes { get; init; } = System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.Static;

    /// <summary>
    /// The implementation attributes: <c>noinlining</c>, <c>synchronized</c>, and the rest.
    /// </summary>
    public System.Reflection.MethodImplAttributes ImplAttributes { get; init; }

    /// <summary>
    /// The calling convention; <c>vararg</c> for a vararg member.
    /// </summary>
    public System.Reflection.CallingConventions CallingConvention { get; init; } = System.Reflection.CallingConventions.Standard;

    /// <summary>
    /// The <c>modreq</c> types on the return type.
    /// </summary>
    public IReadOnlyList<Type> ReturnRequiredModifiers { get; init; } = [];

    /// <summary>
    /// The <c>modopt</c> types on the return type.
    /// </summary>
    public IReadOnlyList<Type> ReturnOptionalModifiers { get; init; } = [];

    /// <summary>
    /// The generic parameters of a generic method, addressed by <c>!!N</c>.
    /// </summary>
    public IReadOnlyList<GenericParameterDeclaration> TypeParameters { get; init; } = [];

    /// <summary>
    /// The custom attributes declared on the method.
    /// </summary>
    public IReadOnlyList<CustomAttributeDeclaration> CustomAttributes { get; init; } = [];

    /// <summary>
    /// True for a static method.
    /// </summary>
    public bool IsStatic => Attributes.HasFlag(System.Reflection.MethodAttributes.Static);

    /// <summary>
    /// Renders a member the way a listing shows it: <c>instance int32 Sum()</c>, <c>static int32 Make(int32)</c>.
    /// </summary>
    /// <returns>The member form.</returns>
    public string DescribeMember() => (IsStatic ? "static " : "instance ") + Describe();

    /// <summary>
    /// The parameter types in order.
    /// </summary>
    public Type[] ParameterTypes => [.. Parameters.Select(p => p.Type)];

    /// <summary>
    /// Renders the signature the way a call names it, for example <c>int32 Fib(int32)</c>.
    /// </summary>
    /// <returns>The call form.</returns>
    public string Describe() =>
        $"{TypeNameFormatter.Pretty(ReturnType)} {Name}({string.Join(", ", Parameters.Select(p => TypeNameFormatter.Pretty(p.Type)))})";

    /// <summary>
    /// Renders the signature with its parameter names, for example <c>int32 Fib(int32 n)</c>.
    /// </summary>
    /// <returns>The header form.</returns>
    public string DescribeWithNames() =>
        $"{TypeNameFormatter.Pretty(ReturnType)} {Name}({string.Join(", ", Parameters.Select(p => (TypeNameFormatter.Pretty(p.Type) + " " + (p.Name ?? "")).TrimEnd()))})";
}
