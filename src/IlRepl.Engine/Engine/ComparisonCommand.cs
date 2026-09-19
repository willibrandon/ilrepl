namespace IlRepl.Engine;

/// <summary>
/// Parses comparison calls and their explicit execution conditions without running user code.
/// </summary>
internal static partial class ComparisonCommand
{
    /// <summary>
    /// Parses a named behavioral comparison with its literal or scenario workload and execution limits.
    /// </summary>
    /// <param name="text">The text following .compare.</param>
    /// <returns>The validated comparison options.</returns>
    internal static ComparisonOptions Parse(string text)
    {
        var space = text.IndexOf(' ');
        if (space < 0)
        {
            throw new ReplException("usage: .compare Name (<literals>) | Name using Scenario [--assert] "
                + "[--timeout 30s] [--stdin \"text\"] [--files directory]");
        }

        var name = text[..space];
        var rest = text[(space + 1)..].Trim();
        IReadOnlyList<string> arguments = [];
        string? scenario = null;
        if (rest.StartsWith('('))
        {
            arguments = ReadArguments(ref rest);
        }
        else if (rest.StartsWith("using ", StringComparison.Ordinal))
        {
            rest = rest[6..].Trim();
            if (rest.Length == 0)
            {
                throw new ReplException("using requires a parameterless session scenario");
            }

            scenario = InstructionParser.Unquote(Word(ref rest));
        }
        else
        {
            throw new ReplException("supply literal arguments in parentheses or use 'using Scenario'");
        }

        var assert = false;
        var timeout = 30000;
        var stdin = "";
        string? directory = null;
        while (rest.Length != 0)
        {
            var flag = Word(ref rest);
            switch (flag)
            {
                case "--assert":
                    assert = true;
                    break;
                case "--timeout":
                {
                    timeout = ComparisonDuration.Parse(Word(ref rest));
                    break;
                }

                case "--stdin":
                    stdin = LiteralParser.ParseString(Word(ref rest));
                    break;
                case "--files":
                    directory = Word(ref rest);
                    if (directory.StartsWith('"') || directory.StartsWith('\''))
                    {
                        directory = LiteralParser.ParseString(directory);
                    }

                    break;
                default:
                    throw new ReplException($"unknown comparison option '{flag}'");
            }
        }

        return new ComparisonOptions(name, arguments, scenario, assert, timeout, stdin, directory);
    }

    private static string Word(ref string text)
    {
        if (text.Length == 0)
        {
            throw new ReplException("a comparison option is missing its value");
        }

        var end = 0;
        var quote = text[0] is '\'' or '"' ? text[0] : '\0';
        if (quote != '\0')
        {
            var closed = false;
            end = 1;
            while (end < text.Length)
            {
                if (text[end++] == quote)
                {
                    closed = true;
                    break;
                }

                if (text[end - 1] == '\\' && end < text.Length)
                {
                    end++;
                }
            }

            if (!closed)
            {
                throw new ReplException("unterminated quoted comparison option");
            }
        }
        else
        {
            while (end < text.Length && !char.IsWhiteSpace(text[end]))
            {
                end++;
            }
        }

        var word = text[..end];
        text = text[end..].TrimStart();
        return word;
    }
}
