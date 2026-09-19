using System.Text;
using IlRepl.Protocol;

namespace IlRepl.Engine;

/// <summary>
/// Shortens proven native symbols for presentation without changing their assembly-qualified equality identities.
/// </summary>
public static class NativeSymbolDisplay
{
    /// <summary>
    /// Uses captured CIL names where unambiguous and retains full identities when two names would look identical.
    /// </summary>
    /// <param name="line">An identity-preserving normalized instruction or diff line.</param>
    /// <param name="addresses">Address facts from every side being displayed.</param>
    /// <returns>A concise presentation of the same instruction.</returns>
    public static string Format(string line, IReadOnlyList<NativeAddressFact> addresses)
    {
        var names = addresses.Where(fact => fact.DisplaySymbol.Length != 0)
            .GroupBy(fact => (fact.Kind, fact.DisplaySymbol))
            .Where(group => group.Select(fact => fact.Symbol).Distinct(StringComparer.Ordinal).Count() == 1)
            .Select(group => group.First()).OrderByDescending(fact => fact.Symbol.Length).ToArray();
        if (names.Length == 0)
        {
            return line;
        }

        var result = new StringBuilder();
        for (var index = 0; index < line.Length; index++)
        {
            if (line[index] is '"' or '\'')
            {
                var quote = line[index];
                result.Append(line[index++]);
                while (index < line.Length)
                {
                    var character = line[index];
                    result.Append(character);
                    if (character == '\\' && index + 1 < line.Length)
                    {
                        result.Append(line[++index]);
                    }
                    else if (character == quote)
                    {
                        break;
                    }

                    index++;
                }

                continue;
            }

            var matched = false;
            if (line[index] == '<')
            {
                foreach (var fact in names)
                {
                    var key = "<" + fact.Kind + ":" + fact.Symbol;
                    var end = index + key.Length;
                    if (end >= line.Length || !line.AsSpan(index).StartsWith(key, StringComparison.Ordinal)
                        || line[end] != '>' && !line.AsSpan(end).StartsWith("+0x", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    result.Append('<').Append(fact.Kind).Append(':').Append(fact.DisplaySymbol);
                    index = end - 1;
                    matched = true;
                    break;
                }
            }

            if (!matched)
            {
                result.Append(line[index]);
            }
        }

        return result.ToString();
    }
}
