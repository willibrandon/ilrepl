using System.Globalization;
using System.Text.RegularExpressions;
using IlRepl.Protocol;

namespace IlRepl.Engine;

/// <summary>
/// Parses complete CoreCLR listing blocks while preserving original immediates and encoding bytes.
/// </summary>
public static partial class NativeDisassembly
{
    /// <summary>
    /// Extracts complete compilations from release CoreCLR disassembly and summary output.
    /// </summary>
    /// <param name="text">The unmodified JIT output file.</param>
    /// <returns>Complete listings in emission order.</returns>
    public static NativeCompilation[] Parse(string text)
    {
        var result = new List<NativeCompilation>();
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var header = Header().Match(lines[index]);
            if (!header.Success) continue;
            var start = index;
            var end = index + 1;
            while (end < lines.Length && !Header().IsMatch(lines[end]) && !Size().IsMatch(lines[end])) end++;
            if (end == lines.Length || !Size().IsMatch(lines[end])) continue;
            var block = string.Join('\n', lines[start..(end + 1)]);
            if (!block.Contains("; BEGIN METHOD ", StringComparison.Ordinal) || !block.Contains("; END METHOD ", StringComparison.Ordinal))
                continue;
            var pgo = block.Contains("Dynamic PGO", StringComparison.Ordinal) ? "Dynamic"
                : block.Contains("Synthesized PGO", StringComparison.Ordinal) ? "Synthesized"
                : block.Contains("Static PGO", StringComparison.Ordinal) ? "Static"
                : block.Contains("No PGO data", StringComparison.Ordinal) ? "None"
                : header.Groups[2].Value is "Tier0" or "Instrumented Tier0" ? "Not applicable" : "Unknown";
            result.Add(new NativeCompilation
            {
                Method = header.Groups[1].Value, Tier = header.Groups[2].Value, Pgo = pgo,
                CodeSize = int.Parse(Size().Match(lines[end]).Groups[1].Value, CultureInfo.InvariantCulture), Listing = block,
            });
            index = end;
        }
        return [.. result];
    }

    /// <summary>
    /// Retains selected-signature blocks that are partial or lack an unambiguous publication match.
    /// </summary>
    /// <param name="text">The bounded original output.</param>
    /// <param name="methodNames">The selected concrete and canonical header signatures.</param>
    /// <param name="attributed">The blocks already attributed to published code.</param>
    /// <returns>Unmodified blocks that cannot contribute to a comparison conclusion.</returns>
    public static string[] Unattributed(string text, IReadOnlyList<string> methodNames,
        IReadOnlyList<NativeCompilation> attributed)
    {
        var result = new List<string>();
        var remaining = attributed.Select(compilation => compilation.Listing).ToList();
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var header = Header().Match(lines[index]);
            if (!header.Success || !methodNames.Contains(header.Groups[1].Value, StringComparer.Ordinal)) continue;
            var end = index + 1;
            while (end < lines.Length && !Header().IsMatch(lines[end]) && !Size().IsMatch(lines[end])) end++;
            if (end < lines.Length && Size().IsMatch(lines[end])) end++;
            var block = string.Join('\n', lines[index..end]);
            if (!remaining.Remove(block)) result.Add(block);
            index = end - 1;
        }
        return [.. result];
    }

    /// <summary>
    /// Returns instruction text without incidental listing headers, code bytes, or unique block prefixes.
    /// </summary>
    /// <param name="compilation">A complete original listing.</param>
    /// <param name="raw">Whether encoding bytes and original labels remain visible.</param>
    /// <returns>The instruction lines used for presentation and comparison.</returns>
    public static string[] Instructions(NativeCompilation compilation, bool raw = false)
    {
        var active = false;
        var lines = new List<string>();
        foreach (var text in compilation.Listing.Split('\n'))
        {
            if (text.StartsWith("; BEGIN METHOD ", StringComparison.Ordinal)) { active = true; continue; }
            if (text.TrimStart().StartsWith("; END METHOD ", StringComparison.Ordinal) || Size().IsMatch(text)) continue;
            if (!active || string.IsNullOrWhiteSpace(text)) continue;
            var line = raw ? text.TrimEnd() : Bytes().Replace(text, "    ").TrimEnd();
            if (!raw) line = Label().Replace(Offset().Replace(line, ""), "L$1").TrimEnd();
            lines.Add(line);
        }
        return [.. lines];
    }

    /// <summary>
    /// Retains JIT frame, interruptibility, and instruction-set comments separately from instruction comparison.
    /// </summary>
    /// <param name="compilation">The original attributed compilation.</param>
    /// <param name="raw">Whether all original header lines should be retained.</param>
    /// <returns>The original header, or its useful code-generation details for compact presentation.</returns>
    public static string[] Headers(NativeCompilation compilation, bool raw = false) => [.. compilation.Listing.Split('\n')
        .TakeWhile(line => !line.StartsWith("; BEGIN METHOD ", StringComparison.Ordinal))
        .Where(line => !string.IsNullOrWhiteSpace(line) && (raw || line.Contains("based frame", StringComparison.Ordinal)
            || line.Contains("interruptible", StringComparison.Ordinal) || line.StartsWith("; Emitting ", StringComparison.Ordinal)))];

    [GeneratedRegex(@"^; Assembly listing for method (.+) \(([^()]*)\)$", RegexOptions.CultureInvariant)]
    private static partial Regex Header();

    [GeneratedRegex(@"^; Total bytes of code (\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex Size();

    [GeneratedRegex(@"^\s+(?:[0-9A-Fa-f]{2}){1,32}\s+(?=[a-z])", RegexOptions.CultureInvariant)]
    private static partial Regex Bytes();

    [GeneratedRegex(@"G_M\d+_IG(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex Label();

    [GeneratedRegex(@"\s*;;\s*offset=0x[0-9A-Fa-f]+", RegexOptions.CultureInvariant)]
    private static partial Regex Offset();
}
