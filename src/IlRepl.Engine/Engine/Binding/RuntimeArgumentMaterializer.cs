namespace IlRepl.Engine.Binding;

/// <summary>
/// Creates argument values only for actual input, after the shared declaration binder succeeds.
/// </summary>
internal static class RuntimeArgumentMaterializer
{
    /// <summary>
    /// Projects an argument declaration and evaluates its explicit or zero initializer.
    /// </summary>
    /// <param name="declaration">The bound declaration.</param>
    /// <param name="adapter">The actual input's runtime adapter.</param>
    /// <returns>The runtime argument.</returns>
    public static ArgumentDeclaration Materialize(ArgumentSyntax declaration, RuntimeBindingAdapter adapter)
    {
        var type = adapter.ToType(declaration.Type);
        var value = declaration.Literal is not null
            ? ValueLiteralParser.Parse(declaration.Literal, type)
            : type.IsValueType ? Array.CreateInstance(type, 1).GetValue(0) : null;
        return new ArgumentDeclaration(type, declaration.Name, value, declaration.Literal ?? (type.IsValueType ? "default" : "null"));
    }
}
