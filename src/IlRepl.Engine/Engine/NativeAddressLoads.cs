using System.Text.RegularExpressions;

namespace IlRepl.Engine;

/// <summary>
/// Folds the instructions that build one proven address into a single line, so that a comparison does not depend on the address.
/// </summary>
public static partial class NativeAddressLoads
{
    /// <summary>
    /// Replaces each run of Arm64 moves that build the same address in the same register with one move of that address.
    /// </summary>
    /// <param name="lines">The normalized instructions of one compilation.</param>
    /// <returns>The same instructions with every address load on one line.</returns>
    public static string[] Fold(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        // The JIT leaves out the move for a 16-bit part that is zero, so the same load takes two, three, or four
        // instructions depending on where the runtime placed its target in that process.
        var result = new List<string>(lines.Count);
        (string Register, string Label)? building = null;
        foreach (var line in lines)
        {
            var part = Part().Match(line);
            if (!part.Success)
            {
                building = null;
                result.Add(line);
                continue;
            }

            var load = (part.Groups["register"].Value, part.Groups["label"].Value);
            if (building != load)
            {
                result.Add(part.Groups["indent"].Value + "mov     " + load.Item1 + ", " + load.Item2);
                building = load;
            }
        }

        return [.. result];
    }

    // The shift is read as the normalizer reads it: in either case, with or without a comma before it or a "#" in it.
    [GeneratedRegex(@"^(?<indent>\s*)mov[zkn]\s+(?<register>[xw]\d+),\s+~?bits\d+:\d+\((?<label>.*)\)(?:\s*,?\s*lsl\s+#?\d+)?\s*$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Part();
}
