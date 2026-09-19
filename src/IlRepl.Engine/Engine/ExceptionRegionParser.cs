namespace IlRepl.Engine;

/// <summary>
/// Parses ILAsm exception ranges without resolving or executing their labels.
/// </summary>
internal static class ExceptionRegionParser
{
    /// <summary>
    /// Parses one ordered clause while retaining its exclusive label boundaries.
    /// </summary>
    /// <typeparam name="T">The resolved catch-type representation.</typeparam>
    /// <param name="text">The text after the .try directive.</param>
    /// <param name="resolve">Resolves the catch type without executing user code.</param>
    /// <returns>The complete clause with symbolic label boundaries.</returns>
    public static ExceptionRegion<T> Parse<T>(string text, Func<string, T> resolve) where T : class
    {
        var rest = text.Trim();
        string Word()
        {
            var length = rest.IndexOfAny([' ', '\t']);
            if (length < 0)
            {
                var result = rest;
                rest = "";
                return result;
            }

            var word = rest[..length];
            rest = rest[length..].TrimStart();
            return word;
        }

        void Require(string expected)
        {
            if (Word() != expected)
            {
                throw new ReplException($"expected '{expected}' in .try START to END catch T handler START to END");
            }
        }

        var start = Word();
        Require("to");
        var end = Word();
        var kind = Word() switch
        {
            "catch" => IlClauseKind.Catch,
            "filter" => IlClauseKind.Filter,
            "finally" => IlClauseKind.Finally,
            "fault" => IlClauseKind.Fault,
            _ => throw new ReplException("an exception range needs catch, filter, finally, or fault"),
        };

        T? catchType = null;
        string? filter = null;
        if (kind == IlClauseKind.Catch)
        {
            var handler = LastWord(rest, "handler");
            if (handler < 0)
            {
                throw new ReplException("a catch range needs a type followed by handler START to END");
            }

            catchType = resolve(rest[..handler]);
            rest = rest[handler..];
        }
        else if (kind == IlClauseKind.Filter)
        {
            filter = Word();
        }

        Require("handler");
        var handlerStart = Word();
        Require("to");
        var handlerEnd = Word();
        var region = new ExceptionRegion<T>(kind, start, end, handlerStart, handlerEnd, filter, catchType);
        if (rest.Length != 0 || region.Labels.Any(label => label.Length == 0))
        {
            throw new ReplException("an exception range needs nonempty labels and no trailing text");
        }

        return region;
    }

    private static int LastWord(string text, string word)
    {
        for (var index = text.Length - word.Length - 1; index > 0; index--)
        {
            if (char.IsWhiteSpace(text[index - 1]) && char.IsWhiteSpace(text[index + word.Length])
                && text.AsSpan(index).StartsWith(word, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }
}
