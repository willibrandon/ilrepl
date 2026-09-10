using IlRepl.Engine.Binding;

namespace IlRepl.Engine;

/// <summary>
/// Parses ILAsm type syntax and binds it to a runtime type through the shared grammar and binder.
/// </summary>
/// <remarks>
/// Parses ILAsm type syntax into <see cref="Type"/> instances: primitives, <c>[assembly]Namespace.Type</c>,
/// nested <c>Outer/Inner</c>, generic instantiations, <c>!N</c> and <c>!!N</c> parameters, arrays, byrefs,
/// pointers, <c>pinned</c>, <c>modreq</c>/<c>modopt</c>, and <c>method</c> function pointer signatures.
/// The grammar is <see cref="CilSyntaxParser"/>'s and the decisions are <see cref="SymbolBinder"/>'s;
/// this entry point binds in the runtime scope and hands back the runtime type.
/// </remarks>
public static class TypeParser
{
    /// <summary>
    /// Rewrites multi-word IL keywords into single tokens so the rest of the parser can treat them as names.
    /// </summary>
    /// <param name="text">The IL text.</param>
    /// <returns>The normalized text.</returns>
    public static string Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text
            .Replace("native unsigned int", "nativeuint", StringComparison.Ordinal)
            .Replace("native uint", "nativeuint", StringComparison.Ordinal)
            .Replace("native int", "nativeint", StringComparison.Ordinal)
            .Replace("unsigned int8", "uint8", StringComparison.Ordinal)
            .Replace("unsigned int16", "uint16", StringComparison.Ordinal)
            .Replace("unsigned int32", "uint32", StringComparison.Ordinal)
            .Replace("unsigned int64", "uint64", StringComparison.Ordinal);
    }

    /// <summary>
    /// Parses a complete type expression.
    /// </summary>
    /// <param name="text">The type in IL syntax.</param>
    /// <param name="context">The parse context.</param>
    /// <returns>The parsed type.</returns>
    /// <exception cref="ReplException">The text is not a valid type or the type cannot be found.</exception>
    public static Type Parse(string text, ParseContext context)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(context);
        var s = Normalize(text);
        var pos = 0;
        var syntax = CilSyntaxParser.ParseTypeAt(s, ref pos);
        var type = Bind(syntax, context, out _, out _, out _);
        SkipWhitespace(s, ref pos);
        if (pos != s.Length)
        {
            throw new ReplException($"unexpected '{s[pos..]}' after type");
        }

        return type;
    }

    /// <summary>
    /// Parses a type starting at <paramref name="pos"/> and advances past it.
    /// </summary>
    /// <param name="s">The normalized text.</param>
    /// <param name="pos">The position to start at; updated to the first character after the type.</param>
    /// <param name="context">The parse context.</param>
    /// <param name="pinned">Set to true when the type carried the <c>pinned</c> modifier.</param>
    /// <returns>The parsed type.</returns>
    /// <exception cref="ReplException">The text is not a valid type or the type cannot be found.</exception>
    public static Type ParseAt(string s, ref int pos, ParseContext context, out bool pinned) =>
        ParseAt(s, ref pos, context, out pinned, out _, out _);

    /// <summary>
    /// Parses a type starting at <paramref name="pos"/>, advances past it, and reports the custom
    /// modifiers written after it.
    /// </summary>
    /// <param name="s">The normalized text.</param>
    /// <param name="pos">The position to start at; updated to the first character after the type.</param>
    /// <param name="context">The parse context.</param>
    /// <param name="pinned">Set to true when the type carried the <c>pinned</c> modifier.</param>
    /// <param name="requiredModifiers">The <c>modreq</c> types, in order.</param>
    /// <param name="optionalModifiers">The <c>modopt</c> types, in order.</param>
    /// <returns>The parsed type.</returns>
    /// <exception cref="ReplException">The text is not a valid type or the type cannot be found.</exception>
    public static Type ParseAt(string s, ref int pos, ParseContext context, out bool pinned, out List<Type> requiredModifiers, out List<Type> optionalModifiers)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(context);
        var syntax = CilSyntaxParser.ParseTypeAt(s, ref pos);
        return Bind(syntax, context, out pinned, out requiredModifiers, out optionalModifiers);
    }

    private static Type Bind(TypeSyntax syntax, ParseContext context, out bool pinned, out List<Type> requiredModifiers,
        out List<Type> optionalModifiers)
    {
        var scope = new RuntimeBindingScope(context);
        var bound = SymbolBinder.BindType(syntax, scope);
        var adapter = new RuntimeBindingAdapter(scope);
        pinned = bound.Pinned;
        requiredModifiers = [.. bound.RequiredModifiers.Select(adapter.ToType)];
        optionalModifiers = [.. bound.OptionalModifiers.Select(adapter.ToType)];
        return adapter.ToType(bound.Type);
    }

    /// <summary>
    /// Finds the quote that closes a quoted name, skipping escaped characters inside it.
    /// </summary>
    /// <param name="s">The text.</param>
    /// <param name="open">The index of the opening quote.</param>
    /// <returns>The index of the closing quote.</returns>
    /// <exception cref="ReplException">The quote is never closed.</exception>
    public static int EndOfQuoted(string s, int open) => CilSyntaxParser.EndOfQuoted(s, open);

    /// <summary>
    /// Decodes the inside of a quoted name: <c>\\\\</c> is a backslash and <c>\\'</c> a quote, as ILAsm reads them.
    /// </summary>
    /// <param name="inner">The text between the quotes.</param>
    /// <returns>The name.</returns>
    public static string DecodeQuoted(string inner) => CilSyntaxParser.DecodeQuoted(inner);

    /// <summary>
    /// Splits a comma-separated list while respecting nested brackets.
    /// </summary>
    /// <param name="s">The list text.</param>
    /// <returns>The trimmed items.</returns>
    public static List<string> SplitTopLevel(string s) => CilSyntaxParser.SplitTopLevel(s);

    /// <summary>
    /// Skips whitespace starting at <paramref name="pos"/>.
    /// </summary>
    /// <param name="s">The text.</param>
    /// <param name="pos">The position to advance.</param>
    public static void SkipWhitespace(string s, ref int pos) => CilSyntaxParser.SkipWhitespace(s, ref pos);

    /// <summary>
    /// Consumes <paramref name="keyword"/> at <paramref name="pos"/> when it is present as a whole word.
    /// </summary>
    /// <param name="s">The text.</param>
    /// <param name="pos">The position; advanced past the keyword on success.</param>
    /// <param name="keyword">The keyword to match.</param>
    /// <returns>True when the keyword was consumed.</returns>
    public static bool TryKeyword(string s, ref int pos, string keyword) => CilSyntaxParser.TryKeyword(s, ref pos, keyword);

    /// <summary>
    /// Finds the index of the parenthesis that closes the one at <paramref name="open"/>.
    /// </summary>
    /// <param name="s">The text.</param>
    /// <param name="open">The index of the opening parenthesis.</param>
    /// <returns>The index of the matching closing parenthesis.</returns>
    /// <exception cref="ReplException">The parentheses are unbalanced.</exception>
    public static int FindMatchingParen(string s, int open) => CilSyntaxParser.FindMatchingParen(s, open);

    /// <summary>
    /// The primitive type keywords: <c>int32</c>, <c>string</c>, <c>void</c>, the C# spellings, and <c>decimal</c>.
    /// </summary>
    public static IReadOnlyCollection<string> PrimitiveKeywords => [.. CilPrimitives.Spellings];

    /// <summary>
    /// True when <paramref name="c"/> can appear inside an IL type or member name.
    /// </summary>
    /// <param name="c">The character.</param>
    /// <returns>True for letters, digits, and the punctuation IL names allow.</returns>
    public static bool IsNameChar(char c) => CilSyntaxParser.IsNameChar(c);

    /// <summary>
    /// Returns the primitive type an IL keyword names, or null when the word is not a primitive keyword.
    /// </summary>
    /// <param name="keyword">The keyword, for example <c>int32</c>.</param>
    /// <returns>The type, or null.</returns>
    public static Type? PrimitiveKeywordType(string keyword)
    {
        ArgumentNullException.ThrowIfNull(keyword);
        if (!CilPrimitives.TryCanonical(keyword.Trim(), out var canonical))
        {
            return null;
        }

        return canonical == CilPrimitives.DecimalAlias ? typeof(decimal) : CilPrimitives.TypeOf(canonical);
    }

    /// <summary>
    /// Returns the IL keyword for a primitive type, or null when the type has none.
    /// </summary>
    /// <param name="type">The type.</param>
    /// <returns>The keyword, for example <c>int32</c>, or null.</returns>
    public static string? PrimitiveKeyword(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return CilPrimitives.KeywordOf(type);
    }
}
