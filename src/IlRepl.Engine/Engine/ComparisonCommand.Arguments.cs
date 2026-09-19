namespace IlRepl.Engine;

/// <summary>
/// Reads comparison literals without treating punctuation inside strings or characters as argument separators.
/// </summary>
internal static partial class ComparisonCommand
{
    private static List<string> ReadArguments(ref string text)
    {
        var arguments = new List<string>();
        var start = 1;
        var depth = 0;
        var quote = '\0';
        for (var index = start; index < text.Length; index++)
        {
            var character = text[index];
            if (quote != '\0')
            {
                if (character == '\\')
                {
                    index++;
                }
                else if (character == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (character is '\'' or '"')
            {
                quote = character;
            }
            else if (character == '(')
            {
                depth++;
            }
            else if (character == ')' && depth > 0)
            {
                depth--;
            }
            else if (depth == 0 && character is ',' or ')')
            {
                var value = text[start..index].Trim();
                if (value.Length != 0)
                {
                    arguments.Add(value);
                }
                else if (character == ',' || arguments.Count != 0)
                {
                    throw new ReplException("a comparison argument is missing");
                }

                if (character == ')')
                {
                    text = text[(index + 1)..].TrimStart();
                    return arguments;
                }

                start = index + 1;
            }
        }

        throw new ReplException(quote == '\0' ? "unterminated comparison arguments" : "unterminated quoted comparison argument");
    }
}
