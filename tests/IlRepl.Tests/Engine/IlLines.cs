using IlRepl.Engine;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Expands compact test specs into the lines the REPL takes: a header ending in <c>{ a; b }</c>
/// becomes the header with <c>{</c>, one line per statement, and a closing brace.
/// </summary>
internal static class IlLines
{
    /// <summary>
    /// Expands every spec.
    /// </summary>
    /// <param name="specs">Lines, some with a one-line body.</param>
    /// <returns>The lines as typed at the prompt.</returns>
    public static IEnumerable<string> Expand(params string[] specs)
    {
        foreach (var spec in specs)
        {
            var open = spec.IndexOf("{ ", StringComparison.Ordinal);
            var isHeader = spec.StartsWith(".method ", StringComparison.Ordinal) || spec.StartsWith(".class ", StringComparison.Ordinal);
            if (!isHeader || open < 0 || !spec.EndsWith('}') || spec.EndsWith("{ }", StringComparison.Ordinal))
            {
                yield return spec;
                continue;
            }

            yield return spec[..(open + 1)];
            foreach (var statement in spec[(open + 2)..^1].Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                yield return statement;
            }

            yield return "}";
        }
    }

    /// <summary>
    /// Loads expanded specs into a fresh session.
    /// </summary>
    /// <param name="specs">The specs.</param>
    /// <returns>The session.</returns>
    public static Session Load(params string[] specs)
    {
        var session = new Session();
        foreach (var line in Expand(specs))
        {
            session.AddLine(line);
        }

        return session;
    }
}
