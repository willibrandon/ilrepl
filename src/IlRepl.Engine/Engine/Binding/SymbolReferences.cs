namespace IlRepl.Engine.Binding;

/// <summary>
/// Enumerates the type identities carried by complete member signatures, including their custom modifiers.
/// </summary>
internal static class SymbolReferences
{
    /// <summary>
    /// Enumerates the owner, signature types, generic arguments and constraints of a method reference.
    /// </summary>
    /// <param name="method">The method reference.</param>
    /// <returns>All types mentioned by the signature.</returns>
    public static IEnumerable<TypeSymbol> Method(MethodSymbol method)
    {
        if (method.DeclaringType is { } owner)
        {
            yield return owner;
        }

        yield return method.ReturnType;
        foreach (var type in method.ReturnRequiredModifiers.Concat(method.ReturnOptionalModifiers)
            .Concat(method.GenericArguments).Concat(method.GenericParameters.SelectMany(parameter => parameter.Constraints)))
        {
            yield return type;
        }

        foreach (var parameter in method.Parameters)
        {
            yield return parameter.Type;
            foreach (var modifier in parameter.RequiredModifiers.Concat(parameter.OptionalModifiers))
            {
                yield return modifier;
            }
        }
    }

    /// <summary>
    /// Enumerates the owner, field type and custom modifiers of a field reference.
    /// </summary>
    /// <param name="field">The field reference.</param>
    /// <returns>All types mentioned by the signature.</returns>
    public static IEnumerable<TypeSymbol> Field(FieldSymbol field)
    {
        yield return field.DeclaringType;
        yield return field.FieldType;
        foreach (var modifier in field.RequiredModifiers.Concat(field.OptionalModifiers))
        {
            yield return modifier;
        }
    }
}
