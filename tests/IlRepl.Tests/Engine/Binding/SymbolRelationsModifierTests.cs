using System.Reflection;
using System.Runtime.InteropServices;
using IlRepl.Engine.Binding;

namespace IlRepl.Tests.Engine.Binding;

/// <summary>
/// Checks exact signature shapes introduced while generic members are instantiated.
/// </summary>
public sealed partial class SymbolRelationsTests
{
    /// <summary>
    /// Generic substitution promotes bounded arrays and function pointers into exact signature types.
    /// </summary>
    [TestMethod]
    public void Instantiate_ExactOnlyArguments_PreservesEveryMemberType()
    {
        var list = RuntimeSymbolImporter.Import(typeof(List<>));
        var typeParameter = RuntimeSymbolImporter.Import(typeof(List<>).GetGenericArguments()[0]);
        var boundedArray = TypeSymbol.Array(TypeSymbol.Primitive("int32"), 1, [], [0]);
        var listOfBoundedArray = TypeSymbol.Construct(list, [boundedArray]);
        var method = new MethodSymbol
        {
            Definition = DefinitionId.ForDeclaration(1, 1),
            Source = MethodSymbolSource.Declared,
            DeclaringType = list,
            Name = "Identity",
            ReturnType = typeParameter,
            Parameters = [new ParameterSymbol(typeParameter, "value")],
        };
        var field = new FieldSymbol
        {
            Definition = DefinitionId.ForDeclaration(1, 2),
            Source = MethodSymbolSource.Declared,
            DeclaringType = list,
            Name = "Value",
            FieldType = typeParameter,
        };

        var instantiatedMethod = SymbolRelations.Instantiate(method, listOfBoundedArray, []);
        var instantiatedField = SymbolRelations.Instantiate(field, listOfBoundedArray);

        Assert.IsTrue(SymbolIdentity.Equal(boundedArray, instantiatedMethod.ExactReturnType));
        Assert.IsTrue(SymbolIdentity.Equal(boundedArray, instantiatedMethod.Parameters.Single().ExactType));
        Assert.IsTrue(SymbolIdentity.Equal(boundedArray, instantiatedField.ExactType));

        var methodId = DefinitionId.ForDeclaration(1, 3);
        var methodParameter = new GenericParameterSymbol(
            methodId,
            true,
            0,
            "T",
            GenericParameterAttributes.None,
            []);
        var functionPointer = TypeSymbol.FunctionPointer(new MethodSignatureSymbol(
            CallingConventions.Standard,
            false,
            CallingConvention.Winapi,
            TypeSymbol.Void,
            [TypeSymbol.Primitive("int32")],
            null));
        var genericMethod = new MethodSymbol
        {
            Definition = methodId,
            Source = MethodSymbolSource.Declared,
            DeclaringType = TypeSymbol.Object,
            Name = "Convert",
            ReturnType = methodParameter.AsType,
            Parameters = [new ParameterSymbol(methodParameter.AsType, "value")],
            GenericParameters = [methodParameter],
        };

        var instantiatedGeneric = SymbolRelations.Instantiate(genericMethod, TypeSymbol.Object, [functionPointer]);

        Assert.IsTrue(SymbolIdentity.Equal(functionPointer, instantiatedGeneric.ExactReturnType));
        Assert.IsTrue(SymbolIdentity.Equal(functionPointer, instantiatedGeneric.Parameters.Single().ExactType));
    }
}
