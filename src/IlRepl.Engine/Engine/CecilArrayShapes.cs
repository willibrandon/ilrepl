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
    /// <returns>The imported type with exact array dimensions.</returns>
    public static TypeReference Restore(TypeReference type, TypeSymbol signature)
    {
        if (type is RequiredModifierType required)
        {
            return new RequiredModifierType(required.ModifierType, Restore(required.ElementType, signature));
        }

        if (type is OptionalModifierType optional)
        {
            return new OptionalModifierType(optional.ModifierType, Restore(optional.ElementType, signature));
        }

        switch (type)
        {
            case ArrayType array when signature.IsArray:
            {
                var element = Restore(array.ElementType, signature.Element!);
                if (signature.Kind == TypeSymbolKind.SzArray)
                {
                    return new ArrayType(element);
                }

                if (signature.Sizes.Count == 0 && signature.LowerBounds.Count == 0)
                {
                    return type;
                }

                var restored = new ArrayType(element, signature.Rank);
                for (var index = 0; index < signature.Rank; index++)
                {
                    var lower = index < signature.LowerBounds.Count ? (int?)signature.LowerBounds[index] : null;
                    var upper = index < signature.Sizes.Count ? (lower ?? 0) + signature.Sizes[index] - 1 : (int?)null;
                    restored.Dimensions[index] = new ArrayDimension(lower, upper);
                }

                return restored;
            }
            case ByReferenceType byRef when signature.Kind == TypeSymbolKind.ByRef:
                return new ByReferenceType(Restore(byRef.ElementType, signature.Element!));
            case PointerType pointer when signature.Kind == TypeSymbolKind.Pointer:
                return new PointerType(Restore(pointer.ElementType, signature.Element!));
            case GenericInstanceType generic when signature.Kind == TypeSymbolKind.Constructed:
            {
                var restored = new GenericInstanceType(generic.ElementType);
                for (var index = 0; index < generic.GenericArguments.Count; index++)
                {
                    restored.GenericArguments.Add(Restore(generic.GenericArguments[index], signature.Arguments[index]));
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
                    ReturnType = Restore(pointer.ReturnType, signature.Signature!.ReturnType),
                };
                for (var index = 0; index < pointer.Parameters.Count; index++)
                {
                    restored.Parameters.Add(new ParameterDefinition(
                        Restore(pointer.Parameters[index].ParameterType, signature.Signature.Parameters[index])));
                }

                return restored;
            }
            default:
                return type;
        }
    }
}
