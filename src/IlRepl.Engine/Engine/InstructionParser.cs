using IlRepl.Engine.Binding;
using IlRepl.Protocol;

namespace IlRepl.Engine;

/// <summary>
/// Turns one line of IL text into an <see cref="Instruction"/>: it strips comments, peels off
/// labels, looks up the opcode, and parses the operand in whatever form that opcode takes.
/// </summary>
public static class InstructionParser
{
    /// <summary>
    /// Removes <c>//</c> and <c>/* */</c> comments from a line on its own, leaving strings and
    /// quoted names untouched. The session removes them with the state a <c>/*</c> carries from
    /// line to line, see <see cref="Session.Normalize"/>; this is for a line with no lines around it.
    /// </summary>
    /// <param name="line">The line.</param>
    /// <returns>The line without comments.</returns>
    public static string StripComments(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var closed = false;
        return CilLexer.StripComments(line, ref closed);
    }

    /// <summary>
    /// Splits leading labels from the rest of the line. <c>L1: L2: add</c> defines <c>L1</c> and <c>L2</c>.
    /// </summary>
    /// <param name="text">The line without comments.</param>
    /// <returns>The labels and the remaining text.</returns>
    public static (List<string> Labels, string Remainder) SplitLabels(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var labels = new List<string>();
        text = text.Trim();
        while (true)
        {
            var colon = text.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                break;
            }

            var candidate = text[..colon];
            if (!IsIdentifier(candidate))
            {
                break;
            }

            if (colon + 1 < text.Length && text[colon + 1] == ':')
            {
                break;
            }

            labels.Add(candidate);
            text = text[(colon + 1)..].TrimStart();
        }

        return (labels, text);
    }

    /// <summary>
    /// True when <paramref name="s"/> is a label or local name: a letter or underscore followed by letters, digits, or underscores.
    /// </summary>
    /// <param name="s">The candidate name.</param>
    /// <returns>True for an identifier.</returns>
    public static bool IsIdentifier(string s)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (s.Length == 0 || !(char.IsLetter(s[0]) || s[0] == '_'))
        {
            return false;
        }

        foreach (var c in s)
        {
            if (!(char.IsLetterOrDigit(c) || c == '_'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Strips the single quotes ILAsm allows around a name, so <c>'value'</c> and <c>value</c> name the same thing.
    /// </summary>
    /// <param name="name">The name as written.</param>
    /// <returns>The name without quotes.</returns>
    public static string Unquote(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return name.Length > 2 && name.StartsWith('\'') && name.EndsWith('\'') ? TypeParser.DecodeQuoted(name[1..^1]) : name;
    }

    /// <summary>
    /// Parses an instruction (opcode plus operand) with no labels or comments. The grammar is
    /// <see cref="CilSyntaxParser"/>'s and the operand decisions are <see cref="SymbolBinder"/>'s;
    /// this entry point binds in the runtime scope and hands back the instruction the emitter takes.
    /// </summary>
    /// <param name="text">The instruction text.</param>
    /// <param name="context">The parse context.</param>
    /// <returns>The instruction.</returns>
    /// <exception cref="ReplException">The opcode is unknown or the operand is invalid.</exception>
    public static Instruction Parse(string text, ParseContext context)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(context);
        var syntax = CilSyntaxParser.ParseInstruction(text);
        var scope = new RuntimeBindingScope(context);
        var bound = SymbolBinder.BindInstruction(syntax, scope);
        return new RuntimeBindingAdapter(scope).ToInstruction(bound);
    }

    /// <summary>
    /// Resolves a local operand written as a name or an index.
    /// </summary>
    /// <param name="operand">The operand text.</param>
    /// <param name="context">The parse context.</param>
    /// <returns>The local index.</returns>
    /// <exception cref="ReplException">No such local is declared.</exception>
    public static int ResolveLocal(string operand, ParseContext context)
    {
        ArgumentNullException.ThrowIfNull(operand);
        ArgumentNullException.ThrowIfNull(context);
        return SymbolBinder.BindLocal(operand, new RuntimeBindingScope(context));
    }

    /// <summary>
    /// Resolves an argument operand written as a name or an index.
    /// </summary>
    /// <param name="operand">The operand text.</param>
    /// <param name="context">The parse context.</param>
    /// <returns>The argument index.</returns>
    /// <exception cref="ReplException">No such argument is declared.</exception>
    public static int ResolveArgument(string operand, ParseContext context)
    {
        ArgumentNullException.ThrowIfNull(operand);
        ArgumentNullException.ThrowIfNull(context);
        return SymbolBinder.BindArgument(operand, new RuntimeBindingScope(context));
    }

    /// <summary>
    /// The opcode nearest to a mistyped mnemonic, or null when nothing is near.
    /// </summary>
    /// <param name="typo">The mnemonic as typed.</param>
    /// <returns>The suggestion, or null.</returns>
    internal static string? SuggestOpcode(string typo) => Suggest(typo);

    private static string? Suggest(string typo)
    {
        string? best = null;
        var bestDistance = 3;
        foreach (var name in OpcodeTable.Names)
        {
            if (OpcodeTable.IsReserved(name))
            {
                continue;
            }

            var d = Levenshtein(typo, name);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = name;
            }
        }

        return best;
    }

    private static int Levenshtein(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
