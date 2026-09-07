using System.Reflection;

namespace IlRepl.Engine;

/// <summary>
/// The header of a <c>.property</c> block before its accessors are known.
/// </summary>
/// <param name="Name">The property name.</param>
/// <param name="Type">The property type.</param>
/// <param name="ParameterTypes">The index parameter types.</param>
/// <param name="IsStatic">True when the accessors are static.</param>
/// <param name="Attributes">The property attributes.</param>
/// <param name="OpensBlock">True when the header ended with <c>{</c>.</param>
public sealed record PropertyHeader(string Name, Type Type, IReadOnlyList<Type> ParameterTypes, bool IsStatic, PropertyAttributes Attributes, bool OpensBlock);

/// <summary>
/// The header of an <c>.event</c> block before its accessors are known.
/// </summary>
/// <param name="Name">The event name.</param>
/// <param name="HandlerType">The delegate type.</param>
/// <param name="Attributes">The event attributes.</param>
/// <param name="OpensBlock">True when the header ended with <c>{</c>.</param>
public sealed record EventHeader(string Name, Type HandlerType, EventAttributes Attributes, bool OpensBlock);

/// <summary>
/// An accessor line inside a property or event block: which accessor, and the method it names.
/// </summary>
/// <param name="Kind"><c>get</c>, <c>set</c>, <c>other</c>, <c>addon</c>, <c>removeon</c>, or <c>fire</c>.</param>
/// <param name="Name">The method name.</param>
/// <param name="ReturnType">The method's return type.</param>
/// <param name="ParameterTypes">The method's parameter types.</param>
/// <param name="IsStatic">True for a static accessor.</param>
public sealed record AccessorReference(string Kind, string Name, Type ReturnType, IReadOnlyList<Type> ParameterTypes, bool IsStatic);

/// <summary>
/// Parses <c>.property</c> and <c>.event</c> headers and the accessor lines inside their blocks.
/// Accessors are ordinary methods of the same type, named as ILAsm names them:
/// <c>.get instance int32 Point::get_Length()</c>, with the type optional.
/// </summary>
public static class PropertyEventParser
{
    /// <summary>
    /// Parses the text after <c>.property</c>.
    /// </summary>
    /// <param name="spec">The header text.</param>
    /// <param name="context">The parse context of the open type.</param>
    /// <returns>The header.</returns>
    /// <exception cref="ReplException">The header is malformed.</exception>
    public static PropertyHeader ParseProperty(string spec, ParseContext context)
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

        var paren = s.IndexOf('(', pos);
        if (paren < 0 || !s.EndsWith(')'))
        {
            throw new ReplException("usage: .property [instance] T Name() {  (index parameters go in the parentheses)");
        }

        var type = TypeParser.ParseAt(s, ref pos, context, out _);
        TypeParser.SkipWhitespace(s, ref pos);
        var name = InstructionParser.Unquote(s[pos..paren].Trim());
        if (!InstructionParser.IsIdentifier(name))
        {
            throw new ReplException($"bad property name '{name}'");
        }

        var close = TypeParser.FindMatchingParen(s, paren);
        var parameters = TypeParser.SplitTopLevel(s[(paren + 1)..close]).Select(t => TypeParser.Parse(t, context)).ToList();
        return new PropertyHeader(name, type, parameters, isStatic, attributes, opens);
    }

    /// <summary>
    /// Parses the text after <c>.event</c>.
    /// </summary>
    /// <param name="spec">The header text.</param>
    /// <param name="context">The parse context of the open type.</param>
    /// <returns>The header.</returns>
    /// <exception cref="ReplException">The header is malformed.</exception>
    public static EventHeader ParseEvent(string spec, ParseContext context)
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

        var type = TypeParser.ParseAt(s, ref pos, context, out _);
        TypeParser.SkipWhitespace(s, ref pos);
        var name = InstructionParser.Unquote(s[pos..].Trim());
        if (!InstructionParser.IsIdentifier(name))
        {
            throw new ReplException("usage: .event HandlerType Name {");
        }

        if (!typeof(Delegate).IsAssignableFrom(type) && type is not System.Reflection.Emit.TypeBuilder)
        {
            throw new ReplException($"{TypeNameFormatter.Pretty(type)} is not a delegate type");
        }

        return new EventHeader(name, type, attributes, opens);
    }

    /// <summary>
    /// Parses an accessor line: <c>.get instance int32 Point::get_Length()</c>.
    /// </summary>
    /// <param name="kind">The directive without its dot.</param>
    /// <param name="spec">The text after the directive.</param>
    /// <param name="context">The parse context of the open type.</param>
    /// <returns>The accessor reference.</returns>
    /// <exception cref="ReplException">The line is malformed.</exception>
    public static AccessorReference ParseAccessor(string kind, string spec, ParseContext context)
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

        var paren = s.IndexOf('(', pos);
        if (paren < 0 || !s.EndsWith(')'))
        {
            throw new ReplException($"usage: .{kind} instance RetType Name(params)");
        }

        var returnType = TypeParser.ParseAt(s, ref pos, context, out _);
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
        var parameters = TypeParser.SplitTopLevel(s[(paren + 1)..close]).Select(t => TypeParser.Parse(t, context)).ToList();
        return new AccessorReference(kind, name, returnType, parameters, isStatic);
    }
}
