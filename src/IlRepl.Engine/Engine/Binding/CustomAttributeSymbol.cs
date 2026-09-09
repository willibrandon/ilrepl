namespace IlRepl.Engine.Binding;

/// <summary>
/// Holds a bound attribute constructor and symbolic arguments without creating any attribute instance.
/// </summary>
/// <param name="Constructor">The bound constructor.</param>
/// <param name="Arguments">The positional arguments.</param>
/// <param name="NamedArguments">The named field and property arguments.</param>
internal sealed record CustomAttributeSymbol(
    BoundMethod Constructor,
    IReadOnlyList<AttributeValueSymbol> Arguments,
    IReadOnlyList<NamedAttributeValue> NamedArguments)
{
    /// <summary>
    /// Enumerates all type identities carried by the attribute's constructor and argument values.
    /// </summary>
    /// <returns>The referenced types, including types stored inside arrays and boxed arguments.</returns>
    public IEnumerable<TypeSymbol> ReferencedTypes()
    {
        yield return Constructor.Method.DeclaringType!;
        foreach (var type in Constructor.Method.ParameterTypes)
        {
            yield return type;
        }

        foreach (var argument in Arguments.Concat(NamedArguments.Select(named => named.Value)))
        {
            foreach (var type in TypesIn(argument))
            {
                yield return type;
            }
        }
    }

    private static IEnumerable<TypeSymbol> TypesIn(AttributeValueSymbol argument)
    {
        yield return argument.Type;
        if (argument.Value is TypeSymbol type)
        {
            yield return type;
        }
        else if (argument.Value is IReadOnlyList<AttributeValueSymbol> elements)
        {
            foreach (var element in elements.SelectMany(TypesIn))
            {
                yield return element;
            }
        }
    }
}
