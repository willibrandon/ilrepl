namespace IlRepl.Engine.Binding;

public sealed partial class EditingSession
{
    private static IEnumerable<string> ReferencedMethods(EditingBody body) => body.Instructions
        .Select(instruction => instruction.Operand.Method?.Method)
        .OfType<MethodSymbol>().Where(method => method.Source == MethodSymbolSource.Session).Select(method => method.Name);

    private static IEnumerable<string> ReferencedMethods(EditingTypeBlock block) =>
        block.Bodies.SelectMany(ReferencedMethods).Concat(block.NestedMethodReferences);

    private static IEnumerable<TypeSymbol> ReferencedTypes(EditingTypeBlock block, DeclarationSymbol declaration)
    {
        return block.Bodies.SelectMany(ReferencedTypes).Concat(block.MetadataTypes)
            .Concat(declaration.Fields.SelectMany(SymbolReferences.Field))
            .Concat(declaration.Methods.SelectMany(SymbolReferences.Method))
            .Concat(declaration.Interfaces)
            .Concat(declaration.GenericParameters.SelectMany(parameter => parameter.Constraints))
            .Concat(declaration.BaseType is null ? [] : new[] { declaration.BaseType })
            .Concat(block.Overrides.SelectMany(mapping => SymbolReferences.Method(mapping.Target.Method)))
            .Concat(block.Accessors.SelectMany(ReferencedTypes));
    }

    private static IEnumerable<TypeSymbol> ReferencedTypes(EditingAccessorBlock block)
    {
        if (block.Property is { } property)
        {
            yield return property.Type;
            foreach (var parameter in property.ParameterTypes)
            {
                yield return parameter;
            }
        }

        if (block.Event is { } @event)
        {
            yield return @event.HandlerType;
        }

        foreach (var type in block.ReferencedTypes)
        {
            yield return type;
        }
    }

    private static IEnumerable<TypeSymbol> ReferencedTypes(EditingBody body)
    {
        if (body.Signature is { } signature)
        {
            foreach (var type in SymbolReferences.Method(signature))
            {
                yield return type;
            }
        }

        foreach (var type in body.MetadataTypes.Concat(body.Locals.Concat(body.Arguments).Select(variable => variable.Type))
            .Concat(body.Overrides.SelectMany(mapping => SymbolReferences.Method(mapping.Target.Method))))
        {
            yield return type;
        }

        foreach (var instruction in body.Instructions)
        {
            var operand = instruction.Operand;
            if (operand.Type is { } type)
            {
                yield return type;
            }

            if (operand.Field is { } field)
            {
                foreach (var reference in SymbolReferences.Field(field))
                {
                    yield return reference;
                }
            }

            if (operand.Method is { } bound)
            {
                foreach (var reference in SymbolReferences.Method(bound.Method).Concat(bound.OptionalParameterTypes ?? []))
                {
                    yield return reference;
                }
            }

            if (operand.Signature is { } calli)
            {
                yield return calli.ReturnType;
                foreach (var parameter in calli.FixedParameters.Concat(calli.OptionalParameters ?? []))
                {
                    yield return parameter;
                }
            }
        }
    }
}
