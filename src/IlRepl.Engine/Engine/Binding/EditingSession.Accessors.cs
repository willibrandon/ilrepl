namespace IlRepl.Engine.Binding;

public sealed partial class EditingSession
{
    private void OpenAccessor(string text, EditingTypeBlock owner)
    {
        if (IsDirective(text, ".property"))
        {
            var property = PropertyEventBinding.ParseProperty(text[9..].Trim(), Scope());
            if (owner.Accessors.Any(accessor => accessor.Property is { } existing
                && existing.Name == property.Name && existing.IsStatic == property.IsStatic
                && SymbolIdentity.Equal(existing.Type, property.Type)
                && existing.ParameterTypes.SequenceEqual(property.ParameterTypes)))
            {
                throw new ReplException($"property {property.Name} is already declared on {owner.Path}");
            }

            _state.Accessor = new EditingAccessorBlock { Property = property, BraceSeen = property.OpensBlock };
        }
        else
        {
            var @event = PropertyEventBinding.ParseEvent(text[6..].Trim(), Scope());
            if (owner.Accessors.Any(accessor => accessor.Event?.Name == @event.Name))
            {
                throw new ReplException($"event {@event.Name} is already declared on {owner.Path}");
            }

            _state.Accessor = new EditingAccessorBlock { Event = @event, BraceSeen = @event.OpensBlock };
        }
    }

    private void AddAccessorLine(string text)
    {
        var block = _state.Accessor!;
        if (text == "{" && !block.BraceSeen)
        {
            block.BraceSeen = true;
            return;
        }

        if (text == "}")
        {
            _state.OpenTypes[^1].Accessors.Add(block);
            _state.Accessor = null;
            return;
        }

        var separator = text.IndexOfAny([' ', '\t']);
        var directive = separator < 0 ? text : text[..separator];
        var rest = separator < 0 ? "" : text[separator..].Trim();
        if (directive == ".custom")
        {
            block.ReferencedTypes.AddRange(CustomAttributeBinding.Parse(rest, Scope()).ReferencedTypes());
            return;
        }

        if (directive is not (".get" or ".set" or ".other" or ".addon" or ".removeon" or ".fire"))
        {
            throw new ReplException($"'{directive}' does not belong inside a {block.Word} block; close it with }} first");
        }

        if ((block.Property is not null && directive is ".addon" or ".removeon" or ".fire")
            || (block.Event is not null && directive is ".get" or ".set" or ".other"))
        {
            throw new ReplException($"{directive} does not belong in a {block.Word} block");
        }

        var accessor = PropertyEventBinding.ParseAccessor(directive[1..], rest, Scope());
        if (accessor.Kind != "other" && block.Accessors.Any(previous => previous.Kind == accessor.Kind))
        {
            throw new ReplException($"{block.Word} {block.Name} already has a {directive}");
        }

        block.Accessors.Add(accessor);
    }

    private static void ValidateAccessors(EditingTypeBlock owner, DeclarationSymbol declaration)
    {
        foreach (var block in owner.Accessors)
        {
            foreach (var accessor in block.Accessors)
            {
                if (!declaration.Methods.Any(method => method.IsDeclared && method.Name == accessor.Name
                    && method.IsStatic == accessor.IsStatic && SymbolIdentity.Equal(method.ReturnType, accessor.ReturnType)
                    && method.ParameterTypes.SequenceEqual(accessor.ParameterTypes)))
                {
                    throw new ReplException($"{owner.Path} has no declared accessor {accessor.Name} with that signature");
                }
            }

            if (block.Event is not null && (!block.Accessors.Any(accessor => accessor.Kind == "addon")
                || !block.Accessors.Any(accessor => accessor.Kind == "removeon")))
            {
                throw new ReplException($"event {block.Name} needs .addon and .removeon");
            }
        }
    }
}
