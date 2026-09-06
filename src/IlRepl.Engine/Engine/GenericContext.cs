namespace IlRepl.Engine;

/// <summary>
/// The generic parameters that <c>!N</c> and <c>!!N</c> refer to while a type is being parsed.
/// <c>!N</c> names a type parameter of the declaring type; <c>!!N</c> names a method type parameter.
/// </summary>
/// <param name="TypeArguments">The declaring type's generic arguments, addressed by <c>!N</c>.</param>
/// <param name="MethodArguments">The method's generic arguments, addressed by <c>!!N</c>.</param>
public sealed record GenericContext(IReadOnlyList<Type> TypeArguments, IReadOnlyList<Type> MethodArguments)
{
    /// <summary>
    /// A context with no generic parameters in scope.
    /// </summary>
    public static GenericContext Empty { get; } = new([], []);

    /// <summary>
    /// Returns a copy of this context with the declaring type's arguments replaced.
    /// </summary>
    /// <param name="typeArguments">The new declaring type arguments.</param>
    /// <returns>The new context.</returns>
    public GenericContext WithTypeArguments(IReadOnlyList<Type> typeArguments) => this with { TypeArguments = typeArguments };

    /// <summary>
    /// Returns a copy of this context with the method arguments replaced.
    /// </summary>
    /// <param name="methodArguments">The new method arguments.</param>
    /// <returns>The new context.</returns>
    public GenericContext WithMethodArguments(IReadOnlyList<Type> methodArguments) => this with { MethodArguments = methodArguments };

    /// <summary>
    /// Resolves a <c>!N</c>, <c>!Name</c>, <c>!!N</c>, or <c>!!Name</c> reference.
    /// </summary>
    /// <param name="isMethod">True for <c>!!</c>, false for <c>!</c>.</param>
    /// <param name="reference">The index or the parameter name.</param>
    /// <returns>The generic parameter type.</returns>
    /// <exception cref="ReplException">The reference is out of range or the name is unknown.</exception>
    public Type Resolve(bool isMethod, string reference)
    {
        var list = isMethod ? MethodArguments : TypeArguments;
        var prefix = isMethod ? "!!" : "!";
        if (int.TryParse(reference, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var index))
        {
            if (index < list.Count)
            {
                return list[index];
            }

            var scope = isMethod ? "method" : "type";
            throw new ReplException(list.Count == 0
                ? $"{prefix}{index} is out of range: no {scope} generic parameters are in scope"
                : $"{prefix}{index} is out of range: {list.Count} {scope} generic parameter(s) are in scope");
        }

        foreach (var t in list)
        {
            if (t.Name == reference)
            {
                return t;
            }
        }

        throw new ReplException($"no generic parameter named '{prefix}{reference}' is in scope");
    }
}
