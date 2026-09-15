using System.Globalization;
using System.Reflection;
using IlRepl.Protocol;

namespace IlRepl.Engine;

/// <summary>
/// Finds adjacent satellite files through the culture directory paths used by the runtime loader.
/// </summary>
internal static class ComparisonSatelliteFiles
{
    /// <summary>
    /// Enumerates existing satellite candidates without loading assemblies or executing their code.
    /// </summary>
    /// <param name="parent">The captured parent assembly and its original file location.</param>
    /// <returns>The sorted, distinct satellite paths visible through normal culture probing.</returns>
    internal static IReadOnlyList<string> Paths(ComparisonAssembly parent)
    {
        if (parent.OriginalLocation is not { } location || Path.GetDirectoryName(location) is not { } root
            || !Directory.Exists(root)) return [];
        var fileName = new AssemblyName(parent.Name).Name + ".resources.dll";
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            string culture;
            try
            {
                culture = CultureInfo.GetCultureInfo(Path.GetFileName(directory)).Name;
            }
            catch (CultureNotFoundException)
            {
                continue;
            }

            if (culture.Length == 0) continue;
            var path = Path.Combine(root, culture, fileName);
            if (!File.Exists(path)) path = Path.Combine(root, culture.ToLowerInvariant(), fileName);
            if (File.Exists(path)) paths.Add(path);
        }
        return paths.Order(StringComparer.Ordinal).ToArray();
    }
}
