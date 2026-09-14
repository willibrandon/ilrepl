using Mono.Cecil;

namespace IlRepl.Engine;

/// <summary>
/// Rebinds generic parameters throughout exact Cecil type signatures.
/// </summary>
internal static class CecilGenericSubstitution
{
    internal static TypeReference Apply(TypeReference type, Dictionary<GenericParameter, TypeReference> map)
    {
        TypeReference Map(TypeReference value) => Apply(value, map);
        if (type is GenericInstanceType generic)
        {
            var copy = new GenericInstanceType(generic.ElementType);
            foreach (var argument in generic.GenericArguments)
            {
                copy.GenericArguments.Add(Map(argument));
            }

            return copy;
        }

        if (type is ArrayType array)
        {
            var copy = new ArrayType(Map(array.ElementType), array.Rank);
            for (var index = 0; index < array.Dimensions.Count; index++)
            {
                copy.Dimensions[index] = array.Dimensions[index];
            }

            return copy;
        }

        if (type is FunctionPointerType pointer)
        {
            var copy = new FunctionPointerType
            {
                HasThis = pointer.HasThis,
                ExplicitThis = pointer.ExplicitThis,
                CallingConvention = pointer.CallingConvention,
                ReturnType = Map(pointer.ReturnType),
            };
            foreach (var parameter in pointer.Parameters)
            {
                copy.Parameters.Add(new ParameterDefinition(parameter.Name, parameter.Attributes, Map(parameter.ParameterType)));
            }

            return copy;
        }

        return type switch
        {
            GenericParameter parameter when map.TryGetValue(parameter, out var replacement) => replacement,
            ByReferenceType reference => new ByReferenceType(Map(reference.ElementType)),
            PointerType unmanaged => new PointerType(Map(unmanaged.ElementType)),
            PinnedType pinned => new PinnedType(Map(pinned.ElementType)),
            SentinelType sentinel => new SentinelType(Map(sentinel.ElementType)),
            RequiredModifierType required => new RequiredModifierType(Map(required.ModifierType), Map(required.ElementType)),
            OptionalModifierType optional => new OptionalModifierType(Map(optional.ModifierType), Map(optional.ElementType)),
            _ => type,
        };
    }
}
