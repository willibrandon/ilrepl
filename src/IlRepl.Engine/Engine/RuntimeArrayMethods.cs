using System.Reflection;
using IlRepl.Engine.Binding;

namespace IlRepl.Engine;

/// <summary>
/// Identifies runtime-provided array operations from their owner and exact signature.
/// </summary>
internal static class RuntimeArrayMethods
{
    /// <summary>
    /// Determines whether a reflected method returns the address of an array element.
    /// </summary>
    internal static bool IsAddress(MethodBase? method) => method is MethodInfo
        { Name: "Address", IsStatic: false, DeclaringType: { IsArray: true } array } info
        && info.ReturnType.IsByRef && info.ReturnType.GetElementType() == array.GetElementType()
        && info.GetParameters().Length == array.GetArrayRank()
        && info.GetParameters().All(parameter => parameter.ParameterType == typeof(int));

    /// <summary>
    /// Determines whether a bound method names the runtime-provided array element-address operation.
    /// </summary>
    internal static bool IsAddress(MethodSymbol method) => method is
        { Source: MethodSymbolSource.Loaded, Name: "Address", IsStatic: false, DeclaringType: { IsArray: true } array,
            ReturnType: { Kind: TypeSymbolKind.ByRef } result }
        && SymbolIdentity.Equal(result.Element, array.Element)
        && method.Parameters.Count == (array.Kind == TypeSymbolKind.SzArray ? 1 : array.Rank)
        && method.Parameters.All(parameter => SymbolIdentity.Equal(parameter.Type, TypeSymbol.Primitive("int32")));
}
