using IlRepl.Protocol;

namespace IlRepl.Engine;

/// <summary>
/// Separates an edit's method reference from its optional name using CIL quoting rules.
/// </summary>
internal static class MethodEditReference
{
    /// <summary>
    /// Finds an alias separator outside quoted identifiers and nested signatures.
    /// </summary>
    /// <param name="text">The edit argument without an opening brace.</param>
    /// <returns>The method reference and optional edit name.</returns>
    internal static (string Reference, string? Name) Split(string text)
    {
        var comment = false;
        var depth = 0;
        var separator = -1;
        foreach (var segment in CilLexer.Segments(text, ref comment).Where(segment => segment.Kind == CilSegmentKind.Code))
        {
            for (var index = segment.Start; index < segment.End; index++)
            {
                depth += text[index] switch { '(' or '[' or '<' => 1, ')' or ']' or '>' => -1, _ => 0 };
                if (depth == 0 && index > 0 && index + 2 < segment.End && text.AsSpan(index, 2).SequenceEqual("as")
                    && char.IsWhiteSpace(text[index - 1]) && char.IsWhiteSpace(text[index + 2]))
                {
                    separator = index;
                }
            }
        }

        return separator < 0 ? (text.Trim(), null) : (text[..separator].Trim(), text[(separator + 2)..].Trim());
    }
}
