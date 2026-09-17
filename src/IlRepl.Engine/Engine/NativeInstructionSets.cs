namespace IlRepl.Engine;

/// <summary>
/// Formats observed intrinsic support as concise architectural instruction-set names.
/// </summary>
public static class NativeInstructionSets
{
    /// <summary>
    /// Removes intrinsic API prefixes and groups AVX-512 extensions without inventing CPU capabilities.
    /// </summary>
    /// <param name="types">The full intrinsic type names whose IsSupported property was true in the worker.</param>
    /// <returns>A deterministic instruction-set summary.</returns>
    public static string Format(IReadOnlyList<string> types)
    {
        var names = types.Select(type => type.Split('.').Last().Split('+')[0])
            .Where(name => !name.StartsWith("Vector", StringComparison.Ordinal) && name is not ("X86Base" or "ArmBase"))
            .Select(name => name.ToUpperInvariant() switch
            {
                "SSE41" => "SSE4.1", "SSE42" => "SSE4.2", "ADVSIMD" => "AdvSIMD", var value => value,
            })
            .Distinct(StringComparer.Ordinal).ToList();
        var avx512 = names.Where(name => name.StartsWith("AVX512", StringComparison.Ordinal)).ToArray();
        names.RemoveAll(name => name.StartsWith("AVX512", StringComparison.Ordinal));
        if (avx512.Length != 0)
        {
            string[] order = ["F", "BW", "CD", "DQ", "VL", "VBMI"];
            var extensions = avx512.Select(name => name[6..]).OrderBy(name =>
                Array.IndexOf(order, name) is var index && index >= 0 ? index : int.MaxValue).ThenBy(name => name, StringComparer.Ordinal);
            names.Add("AVX-512" + string.Join('/', extensions));
        }
        return string.Join(", ", names.OrderByDescending(name => name.StartsWith("AVX-512", StringComparison.Ordinal))
            .ThenBy(name => name, StringComparer.Ordinal));
    }
}
