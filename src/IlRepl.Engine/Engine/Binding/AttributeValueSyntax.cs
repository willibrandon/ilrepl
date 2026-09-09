namespace IlRepl.Engine.Binding;

/// <summary>
/// Separates typed attribute values while keeping quoted text and nested arrays together.
/// </summary>
internal static class AttributeValueSyntax
{
    /// <summary>
    /// Splits positional and named typed arguments without interpreting their values.
    /// </summary>
    /// <param name="inner">The contents of the attribute's braces.</param>
    /// <returns>The complete argument tokens.</returns>
    public static List<string> Split(string inner)
    {
        // Values are separated by whitespace, with parentheses, braces, and quotes kept together.
        var tokens = new List<string>();
        var depth = 0;
        var quote = '\0';
        var start = -1;
        for (var i = 0; i < inner.Length; i++)
        {
            var c = inner[i];
            if (quote != '\0')
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (c is '"' or '\'')
            {
                quote = c;
                if (start < 0)
                {
                    start = i;
                }

                continue;
            }

            if (c is '(' or '{' or '[')
            {
                depth++;
            }
            else if (c is ')' or '}' or ']')
            {
                depth--;
            }

            if (char.IsWhiteSpace(c) && depth == 0)
            {
                if (start >= 0)
                {
                    tokens.Add(inner[start..i]);
                    start = -1;
                }
            }
            else if (start < 0)
            {
                start = i;
            }
        }

        if (start >= 0)
        {
            tokens.Add(inner[start..]);
        }

        // "field int32 X = int32(1)" spans several whitespace-separated pieces; rejoin them.
        var merged = new List<string>();
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i] is "field" or "property")
            {
                var end = i + 1;
                while (end < tokens.Count && tokens[end] != "=")
                {
                    end++;
                }

                if (end + 1 >= tokens.Count)
                {
                    throw new ReplException("a named argument is written as field int32 Name = int32(5)");
                }

                merged.Add(string.Join(" ", tokens.Skip(i).Take(end - i + 2)));
                i = end + 1;
                continue;
            }

            merged.Add(tokens[i]);
        }

        return merged;
    }

}
