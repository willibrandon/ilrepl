using System.Reflection;

namespace IlRepl.Engine.Binding;

/// <summary>
/// Describes runtime-provided array members whose signatures have no metadata definition rows.
/// </summary>
internal static class SymbolArrayMethods
{
    /// <summary>
    /// Constructs the array operations and constructors directly from the symbolic element and rank.
    /// </summary>
    internal static IReadOnlyList<MethodSymbol> Create(TypeSymbol array)
    {
        var rank = array.Kind == TypeSymbolKind.SzArray ? 1 : array.Rank;
        var indices = Enumerable.Range(0, rank).Select(_ => new ParameterSymbol(TypeSymbol.Primitive("int32"), null)).ToArray();
        var methods = new List<MethodSymbol>
        {
            CreateMethod(array, "Get", array.Element!, indices),
            CreateMethod(array, "Set", TypeSymbol.Void, [.. indices, new ParameterSymbol(array.Element!, null)]),
            CreateMethod(array, "Address", TypeSymbol.ByRef(array.Element!), indices),
            CreateMethod(array, ".ctor", TypeSymbol.Void, indices),
        };
        if (array.Kind == TypeSymbolKind.Array)
        {
            methods.Add(CreateMethod(array, ".ctor", TypeSymbol.Void, [.. indices, .. indices]));
        }
        else
        {
            // Vector arrays also provide constructors that allocate each contiguous vector level of a jagged array.
            var parameters = indices.ToList();
            for (var element = array.Element; element?.Kind == TypeSymbolKind.SzArray; element = element.Element)
            {
                parameters.Add(new ParameterSymbol(TypeSymbol.Primitive("int32"), null));
                methods.Add(CreateMethod(array, ".ctor", TypeSymbol.Void, [.. parameters]));
            }
        }

        return methods;
    }

    private static MethodSymbol CreateMethod(
        TypeSymbol owner,
        string name,
        TypeSymbol result,
        IReadOnlyList<ParameterSymbol> parameters) => new()
    {
        Definition = RuntimeDefinitions.OfDeclaration(new object(), 0),
        Source = MethodSymbolSource.Loaded,
        DeclaringType = owner,
        Name = name,
        Attributes = MethodAttributes.Public | MethodAttributes.HideBySig
            | (name == ".ctor" ? MethodAttributes.SpecialName | MethodAttributes.RTSpecialName : 0),
        ImplAttributes = MethodImplAttributes.Runtime,
        CallingConvention = CallingConventions.Standard | CallingConventions.HasThis,
        ReturnType = result,
        Parameters = parameters,
        BodyAvailable = false,
    };
}
