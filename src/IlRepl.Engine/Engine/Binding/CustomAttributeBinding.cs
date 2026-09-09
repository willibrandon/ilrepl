using System.Globalization;

namespace IlRepl.Engine.Binding;

/// <summary>
/// Parses typed and blob-form custom attributes through the same symbol binder used by instructions.
/// </summary>
internal static class CustomAttributeBinding
{
    /// <summary>
    /// Binds an attribute constructor and decodes its values without creating runtime objects of user-defined types.
    /// </summary>
    /// <param name="text">The text after .custom.</param>
    /// <param name="scope">The declaration's scope.</param>
    /// <returns>The symbolic attribute.</returns>
    public static CustomAttributeSymbol Parse(string text, IBindingScope scope)
    {
        var equals = DeclarationText.InitializerEquals(text);
        var constructorText = equals < 0 ? text.Trim() : text[..equals].Trim();
        var values = equals < 0 ? "" : text[(equals + 1)..].Trim();
        var constructor = SymbolBinder.BindMethodReference(
            CilSyntaxParser.ParseMethodReference(constructorText), scope, true);
        var owner = constructor.Method.DeclaringType;
        if (owner is null || constructor.Method.Name != ".ctor")
        {
            throw new ReplException("a custom attribute names a constructor: .custom instance void Attr::.ctor(...) = ...");
        }

        var attribute = scope.LookupType("System.Attribute", null, 0, false).Type;
        if (!SymbolRelations.IsAssignable(owner, attribute, scope))
        {
            throw new ReplException($"{scope.Pretty(owner)} is not an attribute type");
        }

        if (values.Length == 0)
        {
            if (constructor.Method.Parameters.Count > 0)
            {
                throw new ReplException($"{scope.Pretty(owner)}'s constructor takes {constructor.Method.Parameters.Count} "
                    + "argument(s); write them after '='");
            }

            return new CustomAttributeSymbol(constructor, [], []);
        }

        if (values.StartsWith('('))
        {
            if (!values.EndsWith(')'))
            {
                throw new ReplException("a custom attribute blob is written as = ( 01 00 ... )");
            }

            return Blob(constructor, values[1..^1], scope);
        }

        if (values.StartsWith('{'))
        {
            if (!values.EndsWith('}'))
            {
                throw new ReplException("typed attribute arguments are written as = { int32(5) string('text') }");
            }

            return Typed(constructor, values[1..^1], scope);
        }

        throw new ReplException("attribute arguments follow '=' as a blob ( 01 00 ... ) or typed values { int32(5) }");
    }

    private static CustomAttributeSymbol Blob(BoundMethod constructor, string text, IBindingScope scope)
    {
        var bytes = new List<byte>();
        foreach (var token in text.Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Length != 2 || !byte.TryParse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
            {
                throw new ReplException($"bad byte '{token}' in attribute blob");
            }

            bytes.Add(value);
        }

        var reader = new AttributeBlobReader([.. bytes], scope);
        if (reader.ReadUInt16() != 1)
        {
            throw new ReplException("an attribute blob starts with the prolog 01 00");
        }

        AttributeValueSymbol[] arguments = [.. constructor.Method.Parameters.Select(
            parameter => reader.ReadValue(parameter.Type, parameter.Name ?? "argument"))];
        int namedCount = reader.ReadUInt16();
        var named = new List<NamedAttributeValue>();
        for (var i = 0; i < namedCount; i++)
        {
            var kind = reader.ReadByte();
            var type = reader.ReadType();
            var name = reader.ReadString() ?? throw new ReplException("a named attribute argument needs a name");
            var value = reader.ReadValue(type, name);
            if (kind is not (0x53 or 0x54))
            {
                throw new ReplException($"bad named argument kind {kind:X2} in attribute blob (53 is a field, 54 a property)");
            }

            named.Add(Named(constructor.Method.DeclaringType!, name, kind == 0x53, value, scope));
        }

        if (!reader.AtEnd)
        {
            throw new ReplException("attribute blob has bytes left over after the arguments");
        }

        return new CustomAttributeSymbol(constructor, arguments, named);
    }

    private static CustomAttributeSymbol Typed(BoundMethod constructor, string text, IBindingScope scope)
    {
        var owner = constructor.Method.DeclaringType!;
        var arguments = new List<AttributeValueSymbol>();
        var named = new List<NamedAttributeValue>();
        foreach (var token in AttributeValueSyntax.Split(text))
        {
            if (token.StartsWith("field ", StringComparison.Ordinal) || token.StartsWith("property ", StringComparison.Ordinal))
            {
                var field = token.StartsWith("field ", StringComparison.Ordinal);
                var rest = token[(field ? 6 : 9)..].Trim();
                var equals = DeclarationText.InitializerEquals(rest);
                if (equals < 0)
                {
                    throw new ReplException("a named argument is written as field int32 Name = int32(5)");
                }

                var declaration = rest[..equals].Trim();
                var position = 0;
                SymbolBinder.BindType(CilSyntaxParser.ParseTypeAt(declaration, ref position), scope);
                var name = InstructionParser.Unquote(declaration[position..].Trim());
                var member = Named(owner, name, field, new AttributeValueSymbol(TypeSymbol.Object, null), scope);
                var target = member.Field?.FieldType ?? member.Property!.Type;
                named.Add(member with { Value = Value(rest[(equals + 1)..], target, scope, name) });
                continue;
            }

            if (arguments.Count >= constructor.Method.Parameters.Count)
            {
                throw new ReplException($"{scope.Pretty(owner)}'s constructor takes fewer arguments than were given");
            }

            var parameter = constructor.Method.Parameters[arguments.Count];
            arguments.Add(Value(token, parameter.Type, scope, parameter.Name ?? "argument"));
        }

        if (arguments.Count != constructor.Method.Parameters.Count)
        {
            throw new ReplException($"{scope.Pretty(owner)}'s constructor takes {constructor.Method.Parameters.Count} arguments, "
                + $"{arguments.Count} were given");
        }

        return new CustomAttributeSymbol(constructor, arguments, named);
    }

    private static AttributeValueSymbol Value(string text, TypeSymbol target, IBindingScope scope, string what)
    {
        var value = text.Trim();
        if (value.StartsWith("type(", StringComparison.Ordinal) && value.EndsWith(')'))
        {
            var type = SymbolBinder.BindType(CilSyntaxParser.ParseType(value[5..^1]), scope).Type;
            return new AttributeValueSymbol(scope.LookupType("System.Type", null, 0, false).Type, type);
        }

        if (SymbolRenderer.IlPath(target) == "System.Type")
        {
            throw new ReplException($"{what} is a type argument; write type(Name)");
        }

        if (value.StartsWith("string(", StringComparison.Ordinal) && value.EndsWith(')'))
        {
            if (target.Keyword is not ("string" or "object"))
            {
                throw new ReplException($"{what} is a {scope.Pretty(target)}, not a string");
            }

            return new AttributeValueSymbol(TypeSymbol.Primitive("string"), LiteralParser.ParseString(value[7..^1]));
        }

        if (target.IsArray)
        {
            if (!value.StartsWith('{') || !value.EndsWith('}'))
            {
                throw new ReplException($"{what} is an array; write its elements as {{ int32(1) int32(2) }}");
            }

            return new AttributeValueSymbol(target, AttributeValueSyntax.Split(value[1..^1])
                .Select(element => Value(element, target.Element!, scope, what)).ToArray());
        }

        if (target.Keyword == "object")
        {
            var open = value.IndexOf('(', StringComparison.Ordinal);
            if (open > 0 && value.EndsWith(')') && TypeParser.PrimitiveKeywordType(value[..open]) is { } primitive)
            {
                var scalar = RuntimeSymbolImporter.Import(primitive);
                return new AttributeValueSymbol(scalar, LiteralBindingRules.Constant(value, scalar, scope, what));
            }

            throw new ReplException($"{what} takes an object; write a typed value such as int32(5), string('x'), or type(Name)");
        }

        return new AttributeValueSymbol(target, LiteralBindingRules.Constant(value, target, scope, what));
    }

    private static NamedAttributeValue Named(
        TypeSymbol owner, string name, bool isField, AttributeValueSymbol value, IBindingScope scope)
    {
        if (isField)
        {
            FieldSymbol? field = scope.Fields(owner).FirstOrDefault(field => field.Name == name && field.IsPublic && !field.IsStatic);
            return field is null
                ? throw new ReplException($"{scope.Pretty(owner)} has no public field '{name}'")
                : new NamedAttributeValue(field, null, value);
        }

        PropertySymbol? property = scope.Properties(owner)
            .SingleOrDefault(property => property.Name == name && property.IsPublic && !property.IsStatic);
        return property is null
            ? throw new ReplException($"{scope.Pretty(owner)} has no public property '{name}'")
            : new NamedAttributeValue(null, property, value);
    }
}
