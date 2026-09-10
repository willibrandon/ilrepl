using IlRepl.Engine.Binding;
using Mono.Cecil;

namespace IlRepl.Engine;

/// <summary>
/// Preserves array bounds when Cecil imports member signatures through reflection.
/// </summary>
internal static class CecilArrayShapes
{
    /// <summary>
    /// Checks whether an imported signature contains an array whose metadata may need restoring.
    /// </summary>
    /// <param name="type">The imported signature type.</param>
    /// <returns>Whether the signature contains an array.</returns>
    public static bool ContainsArray(TypeReference type) => type is ArrayType
        || type is TypeSpecification specification && ContainsArray(specification.ElementType)
        || type is GenericInstanceType generic && generic.GenericArguments.Any(ContainsArray)
        || type is FunctionPointerType pointer
            && (ContainsArray(pointer.ReturnType) || pointer.Parameters.Any(parameter => ContainsArray(parameter.ParameterType)));

    /// <summary>
    /// Restores metadata array shapes while retaining imported owners and generic parameters.
    /// </summary>
    /// <param name="type">The imported signature type.</param>
    /// <param name="signature">The signature with its original metadata bounds.</param>
    /// <param name="fixups">The destination writer's exact shape corrections.</param>
    /// <returns>The imported type with exact array dimensions.</returns>
    public static TypeReference Restore(TypeReference type, TypeSymbol signature, CecilSignatureFixups fixups)
    {
        if (type is RequiredModifierType required)
        {
            return new RequiredModifierType(required.ModifierType, Restore(required.ElementType, signature, fixups));
        }

        if (type is OptionalModifierType optional)
        {
            return new OptionalModifierType(optional.ModifierType, Restore(optional.ElementType, signature, fixups));
        }

        switch (type)
        {
            case ArrayType array when signature.IsArray:
            {
                var element = Restore(array.ElementType, signature.Element!, fixups);
                if (signature.Kind == TypeSymbolKind.SzArray)
                {
                    return new ArrayType(element);
                }

                return fixups.Array(element, signature.Rank, signature.Sizes, signature.LowerBounds);
            }
            case ByReferenceType byRef when signature.Kind == TypeSymbolKind.ByRef:
                return new ByReferenceType(Restore(byRef.ElementType, signature.Element!, fixups));
            case PointerType pointer when signature.Kind == TypeSymbolKind.Pointer:
                return new PointerType(Restore(pointer.ElementType, signature.Element!, fixups));
            case GenericInstanceType generic when signature.Kind == TypeSymbolKind.Constructed:
            {
                var restored = new GenericInstanceType(generic.ElementType);
                for (var index = 0; index < generic.GenericArguments.Count; index++)
                {
                    restored.GenericArguments.Add(Restore(generic.GenericArguments[index], signature.Arguments[index], fixups));
                }

                return restored;
            }
            case FunctionPointerType pointer when signature.Kind == TypeSymbolKind.FunctionPointer:
            {
                var restored = new FunctionPointerType
                {
                    CallingConvention = pointer.CallingConvention,
                    HasThis = pointer.HasThis,
                    ExplicitThis = pointer.ExplicitThis,
                    ReturnType = Restore(pointer.ReturnType, signature.Signature!.ReturnType, fixups),
                };
                for (var index = 0; index < pointer.Parameters.Count; index++)
                {
                    restored.Parameters.Add(new ParameterDefinition(
                        Restore(pointer.Parameters[index].ParameterType, signature.Signature.Parameters[index], fixups)));
                }

                return restored;
            }
            default:
                return type;
        }
    }
}
