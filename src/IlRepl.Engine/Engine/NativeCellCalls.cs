using System.Globalization;
using System.Text.RegularExpressions;

namespace IlRepl.Engine;

/// <summary>
/// Folds an x64 call through a loaded cell address into the call the JIT writes when it reaches the cell directly.
/// </summary>
public static partial class NativeCellCalls
{
    /// <summary>
    /// Replaces each load of a method's cell that only feeds a call through that register with the direct call.
    /// </summary>
    /// <remarks>
    /// The JIT reaches a callee's cell with a 32-bit displacement and writes <c>call [Name]</c>. When the runtime placed the code
    /// and the cell more than 2 GB apart, the JIT loads the cell's address first and writes <c>call [rax]Name</c>. Which of the
    /// two a process gets follows its memory layout and not the code, so a comparison reads both as the direct call.
    /// </remarks>
    /// <param name="lines">The normalized instructions of one compilation.</param>
    /// <returns>The same instructions with every such call in its direct form.</returns>
    public static string[] Fold(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var result = lines.ToList();
        for (var index = 0; index < result.Count; index++)
        {
            var load = Load().Match(result[index]);
            if (!load.Success)
            {
                continue;
            }

            var use = Use(result, index + 1, load.Groups["register"].Value);
            if (use < 0)
            {
                continue;
            }

            var call = Call().Match(result[use]);
            result[use] = call.Groups["head"].Value + "[" + call.Groups["name"].Value + "]";
            if (EndsGroupAfterCall(result, index))
            {
                // The JIT never lets a call be the last instruction before an epilog, so the direct form has a nop here.
                result[index] = load.Groups["indent"].Value + "nop";
                continue;
            }

            result.RemoveAt(index);
            DropEmptiedGroup(result, index);
            index--;
        }

        // A jump table entry is the distance in bytes to its case, and the load makes that longer. The label beside the entry
        // says the same thing in a way that does not depend on instruction lengths.
        return [.. result.Select(line => TableEntry().Replace(line, "<code:${label}>"))];
    }

    private static int Use(List<string> lines, int start, string register)
    {
        for (var index = start; index < lines.Count; index++)
        {
            var label = Label().Match(lines[index]);
            if (label.Success)
            {
                // A jump to this label would arrive without the load, so the load is then not this call's alone.
                if (IsReferenced(lines, label.Groups["label"].Value))
                {
                    return -1;
                }

                continue;
            }

            var call = Call().Match(lines[index]);
            if (call.Success && call.Groups["register"].Value == register)
            {
                return index;
            }

            var operation = Operation().Match(lines[index]).Groups["operation"].Value;
            if (operation is "call" or "ret" || operation.Contains('j') || Mentions(lines[index], register))
            {
                return -1;
            }
        }

        return -1;
    }

    private static bool EndsGroupAfterCall(List<string> lines, int index) => index > 0 && index + 1 < lines.Count
        && Label().IsMatch(lines[index + 1]) && Operation().Match(lines[index - 1]).Groups["operation"].Value == "call";

    // When the load was the only instruction of its group, a label now follows a label. The direct form has one group there,
    // so the second label goes. Nothing jumps to it, because such a jump would have skipped the load.
    private static void DropEmptiedGroup(List<string> lines, int index)
    {
        if (index < 1 || index >= lines.Count || !Label().IsMatch(lines[index - 1]))
        {
            return;
        }

        var dropped = Label().Match(lines[index]);
        if (!dropped.Success || IsReferenced(lines, dropped.Groups["label"].Value))
        {
            return;
        }

        // The labels after it move up by one, which is only safe when every mention of them is one this can rewrite.
        var number = Number(dropped);
        if (lines.Any(line => !Label().IsMatch(line) && Word().Matches(line).Count(word => IsLaterLabel(word.Value, number))
            != Reference().Matches(line).Count(reference => Number(reference) > number)))
        {
            return;
        }

        lines.RemoveAt(index);
        for (var line = 0; line < lines.Count; line++)
        {
            var pattern = Label().IsMatch(lines[line]) ? Label() : Reference();
            lines[line] = pattern.Replace(lines[line], match => Number(match) > number ? MovedUp(match) : match.Value);
        }
    }

    private static string MovedUp(Match match)
    {
        var digits = match.Groups["number"];
        var moved = (Number(match) - 1).ToString("D" + digits.Length, CultureInfo.InvariantCulture);
        return match.Value.Remove(digits.Index - match.Index, digits.Length).Insert(digits.Index - match.Index, moved);
    }

    private static int Number(Match match) => int.Parse(match.Groups["number"].Value, CultureInfo.InvariantCulture);

    private static bool IsLaterLabel(string word, int number) => word.Length > 1 && word[0] == 'L'
        && word.AsSpan(1).IndexOfAnyExceptInRange('0', '9') < 0 && int.Parse(word.AsSpan(1), CultureInfo.InvariantCulture) > number;

    private static bool IsReferenced(List<string> lines, string label) => lines.Any(line => !Label().IsMatch(line)
        && Word().Matches(line).Any(word => word.Value == label));

    private static bool Mentions(string line, string register)
    {
        var stem = register[1..];
        string[] names = char.IsDigit(stem[0]) ? [register, register + "d", register + "w", register + "b"]
            : stem[1] == 'x' ? [register, "e" + stem, stem, stem[0] + "l", stem[0] + "h"]
            : [register, "e" + stem, stem, stem + "l"];
        return Word().Matches(line).Any(word => names.Contains(word.Value, StringComparer.Ordinal));
    }

    [GeneratedRegex(@"^(?<indent>\s*)mov\s+(?<register>r[a-z0-9]+),\s+<entry-point-cell:.*>\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex Load();

    [GeneratedRegex(@"^(?<head>\s*(?:call|jmp|tail\.jmp|rex\.jmp)\s+)\[(?<register>r[a-z0-9]+)\](?<name>\S.*?)\s*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex Call();

    [GeneratedRegex(@"^\s*(?<label>L(?<number>\d+)):\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex Label();

    // A jump's operand, a jump table entry as the normalizer writes it, and the JIT's own note beside that entry.
    [GeneratedRegex(
        @"(?<=^\s*j[a-z]*\s+(?:SHORT\s+)?)L(?<number>\d+)(?=\s*$)|(?<=<code:)L(?<number>\d+)(?=>)|(?<=;\s*case\s+)L(?<number>\d+)\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex Reference();

    [GeneratedRegex(@"(?<=\bdd\s+)[0-9A-Fa-f]+h(?=\s*;\s*case\s+(?<label>L\d+)\b)", RegexOptions.CultureInvariant)]
    private static partial Regex TableEntry();

    [GeneratedRegex(@"^\s*(?<operation>[a-z][a-z0-9.]*)", RegexOptions.CultureInvariant)]
    private static partial Regex Operation();

    [GeneratedRegex(@"[A-Za-z_][A-Za-z0-9_]*", RegexOptions.CultureInvariant)]
    private static partial Regex Word();
}
