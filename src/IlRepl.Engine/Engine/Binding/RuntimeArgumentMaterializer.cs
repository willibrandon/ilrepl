namespace IlRepl.Engine.Binding;

/// <summary>
/// Projects argument declarations while deferring runtime values during document reconstruction.
/// </summary>
internal static class RuntimeArgumentMaterializer
{
    /// <summary>
    /// Projects an argument declaration and evaluates its explicit or zero initializer.
    /// </summary>
    /// <param name="declaration">The bound declaration.</param>
    /// <param name="adapter">The actual input's runtime adapter.</param>
    /// <param name="defer">Whether to retain the initializer until execution.</param>
    /// <returns>The runtime argument.</returns>
    public static ArgumentDeclaration Materialize(ArgumentSyntax declaration, RuntimeBindingAdapter adapter, bool defer = false)
    {
        var type = adapter.ToType(declaration.Type);
        var value = defer ? null : declaration.Literal is not null
            ? ValueLiteralParser.Parse(declaration.Literal, type)
            : type.IsValueType ? Array.CreateInstance(type, 1).GetValue(0) : null;
        return new ArgumentDeclaration(type, declaration.Name, value, declaration.Literal ?? (type.IsValueType ? "default" : "null"))
        {
            ExactType = declaration.ExactType,
            Deferred = defer,
        };
    }
}
