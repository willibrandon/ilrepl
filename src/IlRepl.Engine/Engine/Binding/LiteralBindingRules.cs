namespace IlRepl.Engine.Binding;

/// <summary>
/// Validates declared literals using built-in scalars and metadata without constructing session values.
/// </summary>
internal static class LiteralBindingRules
{
    /// <summary>
    /// Checks the initializer of a cell argument before the runtime creates its value.
    /// </summary>
    /// <param name="literal">The literal, or null for a default value.</param>
    /// <param name="type">The declared type.</param>
    /// <param name="scope">The metadata and declarations in scope.</param>
    public static void Argument(string? literal, TypeSymbol type, IBindingScope scope)
    {
        if (literal is null)
        {
            return;
        }

        var target = NullableElement(type) ?? type;
        if (literal.Trim() == "null")
        {
            if (type.IsValueTypeShape && NullableElement(type) is null)
            {
                throw new ReplException($"null is not a valid {scope.Pretty(type)}");
            }

            return;
        }

        if (ScalarType(target) is { } scalar)
        {
            ValueLiteralParser.Parse(literal, scalar);
            return;
        }

        var underlying = scope.EnumUnderlyingType(target);
        if (underlying is not null)
        {
            var text = literal.Trim();
            if (text.Length > 0 && (char.IsAsciiDigit(text[0]) || text[0] is '-' or '+'))
            {
                ValueLiteralParser.Parse(text, ScalarType(underlying)!);
                return;
            }

            var fields = scope.Fields(target);
            if (text.Split(',').All(name => fields.Any(field => field.IsLiteral
                && string.Equals(field.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))))
            {
                return;
            }

            throw new ReplException($"'{text}' is not a value of {scope.Pretty(type)}");
        }

        throw new ReplException($"cannot write a literal of type {scope.Pretty(type)}; use a primitive, string, enum, or null");
    }

    /// <summary>
    /// Checks an ILAsm metadata constant and returns only its built-in scalar representation.
    /// </summary>
    /// <param name="text">The text after the equals sign.</param>
    /// <param name="type">The declared type.</param>
    /// <param name="scope">The metadata and declarations in scope.</param>
    /// <param name="what">The diagnostic target.</param>
    /// <returns>The scalar constant, without creating a session enum or struct.</returns>
    public static object? Constant(string text, TypeSymbol type, IBindingScope scope, string what)
    {
        var target = NullableElement(type) ?? type;
        if (text.Trim() == "nullref")
        {
            if (type.IsValueTypeShape && NullableElement(type) is null)
            {
                throw new ReplException($"nullref is not a valid {scope.Pretty(type)}");
            }

            return null;
        }

        var underlying = scope.EnumUnderlyingType(target);
        var scalar = ScalarType(underlying ?? target);
        if (scalar is null && scope.BaseOf(target) is { } parent && SymbolRenderer.IlPath(parent) == "System.Enum")
        {
            scalar = typeof(object);
        }

        if (scalar is null)
        {
            throw new ReplException($"cannot write a constant of type {scope.Pretty(type)}");
        }

        return ConstantParser.Parse(text, scalar, what);
    }

    private static TypeSymbol? NullableElement(TypeSymbol type) => type.Kind == TypeSymbolKind.Constructed
        && SymbolRenderer.IlPath(type.Element!) == "System.Nullable`1" ? type.Arguments[0] : null;

    private static Type? ScalarType(TypeSymbol type) => type.Kind == TypeSymbolKind.Primitive
        ? TypeParser.PrimitiveKeywordType(type.Keyword!)
        : SymbolRenderer.IlPath(type) == "System.Decimal" ? typeof(decimal) : null;
}
