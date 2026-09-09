namespace IlRepl.Engine.Binding;

/// <summary>
/// Shared quote-aware punctuation scans for declarations with literal initializers.
/// </summary>
internal static class DeclarationText
{
    /// <summary>
    /// Finds the parameter list after a parsed return type while skipping quoted names and generic owners.
    /// </summary>
    /// <param name="text">The declaration.</param>
    /// <param name="start">The position following its return type.</param>
    /// <returns>The opening parenthesis offset, or -1.</returns>
    public static int ParameterList(string text, int start)
    {
        var depth = 0;
        for (var index = start; index < text.Length; index++)
        {
            var character = text[index];
            if (character == '\'')
            {
                index = CilSyntaxParser.EndOfQuoted(text, index);
            }
            else if (character is '<' or '[')
            {
                depth++;
            }
            else if (character is '>' or ']')
            {
                depth--;
            }
            else if (character == '(' && depth == 0)
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>
    /// Finds the initializer separator outside quoted identifiers and nested type syntax.
    /// </summary>
    /// <param name="text">The declaration.</param>
    /// <returns>The separator offset, or -1.</returns>
    public static int InitializerEquals(string text)
    {
        var depth = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '\'' or '"')
            {
                var quote = c;
                while (++i < text.Length)
                {
                    if (text[i] == '\\')
                    {
                        i++;
                    }
                    else if (text[i] == quote)
                    {
                        break;
                    }
                }
            }
            else if (c is '<' or '[' or '(')
            {
                depth++;
            }
            else if (c is '>' or ']' or ')')
            {
                depth--;
            }
            else if (c == '=' && depth == 0)
            {
                return i;
            }
        }

        return -1;
    }
}
