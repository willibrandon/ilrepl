using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;
using Hex1b;

namespace IlRepl.Tests.EndToEnd;

/// <summary>
/// Keeps our musl copy of the Hex1b native helper in step with the Hex1b package the build references.
/// </summary>
[TestClass]
public sealed partial class NativeHelperTests
{
    /// <summary>
    /// Every helper function the referenced Hex1b assembly calls by name is defined in our copy of the helper's source.
    /// </summary>
    /// <remarks>
    /// Hex1b ships glibc builds of the helper, and we compile the same source for musl. A newer package can call functions an
    /// older copy lacks, which only shows on Alpine, as a terminal that cannot start. This test shows it on every platform.
    /// </remarks>
    [TestMethod]
    public void OurCopy_DefinesEveryFunctionThePackageCalls()
    {
        var source = File.ReadAllText(Path.Join(RepoPaths.Root, "build", "native", "hex1b", "hex1binterop.c"));
        var defined = Definition().Matches(source).Select(match => match.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        using var stream = File.OpenRead(typeof(Hex1bTerminal).Assembly.Location);
        using var image = new PEReader(stream);
        var reader = image.GetMetadataReader();
        var called = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var handle in reader.MethodDefinitions)
        {
            var import = reader.GetMethodDefinition(handle).GetImport();
            if (!import.Module.IsNil && reader.GetString(reader.GetModuleReference(import.Module).Name)
                .Contains("hex1binterop", StringComparison.OrdinalIgnoreCase))
            {
                called.Add(reader.GetString(import.Name));
            }
        }

        Assert.IsNotEmpty(called, "The Hex1b assembly is expected to call its native helper by name.");
        var missing = called.Where(name => !defined.Contains(name)).ToArray();
        Assert.IsEmpty(missing, "build/native/hex1b/hex1binterop.c is older than the Hex1b package and lacks "
            + string.Join(", ", missing) + ". Refresh it as build/native/hex1b/README.md describes.");
    }

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_ \t\*]*?\b(hex1b_[a-z0-9_]+)\s*\(", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex Definition();
}
