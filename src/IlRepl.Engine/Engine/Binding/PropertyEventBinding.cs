using System.Reflection;

namespace IlRepl.Engine.Binding;

/// <summary>
/// Parses property, event and accessor declarations through the shared symbol binder.
/// </summary>
public static class PropertyEventBinding
{
    /// <summary>
    /// Parses the text after <c>.property</c>.
    /// </summary>
    /// <param name="spec">The header text.</param>
    /// <param name="context">The parse context of the open type.</param>
    /// <returns>The header.</returns>
    /// <exception cref="ReplException">The header is malformed.</exception>
    public static PropertyHeaderSymbol ParseProperty(string spec, IBindingScope context)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(context);
        var s = TypeParser.Normalize(spec).Trim();
        var opens = s.EndsWith('{');
        if (opens)
        {
            s = s[..^1].TrimEnd();
        }

        var attributes = PropertyAttributes.None;
        var isStatic = true;
        var pos = 0;
        while (true)
        {
            TypeParser.SkipWhitespace(s, ref pos);
            if (TypeParser.TryKeyword(s, ref pos, "specialname"))
            {
                attributes |= PropertyAttributes.SpecialName;
            }
            else if (TypeParser.TryKeyword(s, ref pos, "rtspecialname"))
            {
                attributes |= PropertyAttributes.RTSpecialName;
            }
            else if (TypeParser.TryKeyword(s, ref pos, "instance"))
            {
                isStatic = false;
            }
            else if (TypeParser.TryKeyword(s, ref pos, "default"))
            {
                continue;
            }
            else
            {
                break;
            }
        }

        var type = BindAt(s, ref pos, context);
        var paren = DeclarationText.ParameterList(s, pos);
        if (paren < 0 || !s.EndsWith(')'))
        {
            throw new ReplException("usage: .property [instance] T Name() {  (index parameters go in the parentheses)");
        }

        TypeParser.SkipWhitespace(s, ref pos);
        var name = InstructionParser.Unquote(s[pos..paren].Trim());
        if (!InstructionParser.IsIdentifier(name))
        {
            throw new ReplException($"bad property name '{name}'");
        }

        var close = TypeParser.FindMatchingParen(s, paren);
        var parameters = TypeParser.SplitTopLevel(s[(paren + 1)..close])
            .Select(text => SymbolBinder.BindType(CilSyntaxParser.ParseType(text), context).Type).ToList();
        return new PropertyHeaderSymbol(name, type, parameters, isStatic, attributes, opens);
    }

    /// <summary>
    /// Parses the text after <c>.event</c>.
    /// </summary>
    /// <param name="spec">The header text.</param>
    /// <param name="context">The parse context of the open type.</param>
    /// <returns>The header.</returns>
    /// <exception cref="ReplException">The header is malformed.</exception>
    public static EventHeaderSymbol ParseEvent(string spec, IBindingScope context)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(context);
        var s = TypeParser.Normalize(spec).Trim();
        var opens = s.EndsWith('{');
        if (opens)
        {
            s = s[..^1].TrimEnd();
        }

        var attributes = EventAttributes.None;
        var pos = 0;
        while (true)
        {
            TypeParser.SkipWhitespace(s, ref pos);
            if (TypeParser.TryKeyword(s, ref pos, "specialname"))
            {
                attributes |= EventAttributes.SpecialName;
            }
            else if (TypeParser.TryKeyword(s, ref pos, "rtspecialname"))
            {
                attributes |= EventAttributes.RTSpecialName;
            }
            else
            {
                break;
            }
        }

        var type = BindAt(s, ref pos, context);
        TypeParser.SkipWhitespace(s, ref pos);
        var name = InstructionParser.Unquote(s[pos..].Trim());
        if (!InstructionParser.IsIdentifier(name))
        {
            throw new ReplException("usage: .event HandlerType Name {");
        }

        if (!SymbolRelations.IsAssignable(type, context.LookupType("System.Delegate", null, 0, false).Type, context))
        {
            throw new ReplException($"{context.Pretty(type)} is not a delegate type");
        }

        return new EventHeaderSymbol(name, type, attributes, opens);
    }

    /// <summary>
    /// Parses an accessor line: <c>.get instance int32 Point::get_Length()</c>.
    /// </summary>
    /// <param name="kind">The directive without its dot.</param>
    /// <param name="spec">The text after the directive.</param>
    /// <param name="context">The parse context of the open type.</param>
    /// <returns>The accessor reference.</returns>
    /// <exception cref="ReplException">The line is malformed.</exception>
    public static AccessorReferenceSymbol ParseAccessor(string kind, string spec, IBindingScope context)
    {
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(context);
        var s = TypeParser.Normalize(spec).Trim();
        var pos = 0;
        var isStatic = true;
        while (true)
        {
            TypeParser.SkipWhitespace(s, ref pos);
            if (TypeParser.TryKeyword(s, ref pos, "instance"))
            {
                isStatic = false;
            }
            else if (TypeParser.TryKeyword(s, ref pos, "default"))
            {
                continue;
            }
            else
            {
                break;
            }
        }

        var returnType = BindAt(s, ref pos, context);
        var paren = DeclarationText.ParameterList(s, pos);
        if (paren < 0 || !s.EndsWith(')'))
        {
            throw new ReplException($"usage: .{kind} instance RetType Name(params)");
        }

        TypeParser.SkipWhitespace(s, ref pos);
        var name = s[pos..paren].Trim();
        var separator = name.LastIndexOf("::", StringComparison.Ordinal);
        if (separator >= 0)
        {
            name = name[(separator + 2)..];
        }

        name = InstructionParser.Unquote(name);
        if (!InstructionParser.IsIdentifier(name))
        {
            throw new ReplException($"bad accessor name '{name}'");
        }

        var close = TypeParser.FindMatchingParen(s, paren);
        var parameters = TypeParser.SplitTopLevel(s[(paren + 1)..close])
            .Select(text => SymbolBinder.BindType(CilSyntaxParser.ParseType(text), context).Type).ToList();
        return new AccessorReferenceSymbol(kind, name, returnType, parameters, isStatic);
    }
    private static TypeSymbol BindAt(string text, ref int position, IBindingScope scope) =>
        SymbolBinder.BindType(CilSyntaxParser.ParseTypeAt(text, ref position), scope).Type;
}
