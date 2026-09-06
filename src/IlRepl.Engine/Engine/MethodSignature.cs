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
