using IlRepl.Engine;
using IlRepl.Protocol;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Verifies concise native operand presentation never changes stable comparison identities or quoted values.
/// </summary>
[TestClass]
public sealed class NativeSymbolDisplayTests
{
    /// <summary>
    /// Proven symbols use captured CIL spelling while preserving offsets and literal text that merely resembles a symbol.
    /// </summary>
    [TestMethod]
    public void Format_ShortensProvenSymbolButPreservesOffsetAndQuotedLiteral()
    {
        const string symbol = "Example.Owner, Example, Version=1.2.3.4, Culture=neutral, PublicKeyToken=null::Value:System.Int32";
        var fact = new NativeAddressFact { Kind = "static-field", Symbol = symbol, DisplaySymbol = "int32 Example.Owner::Value" };
        var marker = "<static-field:" + symbol + "+0x4>";
        var line = "mov rax, " + marker + " ; literal \"" + marker + "\"";

        var display = NativeSymbolDisplay.Format(line, [fact]);

        Assert.AreEqual("mov rax, <static-field:int32 Example.Owner::Value+0x4> ; literal \"" + marker + "\"", display);
        Assert.AreEqual(symbol, fact.Symbol);
        Assert.Contains(marker, line);
    }

    /// <summary>
    /// Identical short names from distinct assemblies stay visibly distinct and cannot compare equal after normalization.
    /// </summary>
    [TestMethod]
    public void Format_CollidingShortNamesRetainQualifiedEqualityIdentities()
    {
        var first = new NativeAddressFact
        {
            Address = 0x12345678, Kind = "type", Symbol = "Example.Owner, First, Version=1.0.0.0",
            DisplaySymbol = "Example.Owner", Evidence = "RuntimeTypeHandle.Value",
        };
        var second = first with { Address = 0x87654321, Symbol = "Example.Owner, Second, Version=1.0.0.0" };
        var left = Normalize(first);
        var right = Normalize(second);

        Assert.AreNotEqual(left[0], right[0]);
        Assert.Contains("First, Version=1.0.0.0", left[0]);
        Assert.Contains("Second, Version=1.0.0.0", right[0]);
        Assert.AreEqual(left[0], NativeSymbolDisplay.Format(left[0], [first, second]));
        Assert.AreEqual(right[0], NativeSymbolDisplay.Format(right[0], [first, second]));
        Assert.AreEqual("    mov rax, <type:Example.Owner>", NativeSymbolDisplay.Format(left[0], [first]));
        Assert.IsNotEmpty(NativeDifference.Create(left, right, "left", "right"));
        Assert.Contains("First, Version=1.0.0.0", left[0], "Rendering must never replace the typed normalized identity.");
    }

    private static string[] Normalize(NativeAddressFact fact)
    {
        var listing = new NativeCompilation
        {
            Listing = "; BEGIN METHOD Owner:Value\n    mov rax, 0x" + fact.Address.ToString("X")
                + "\n    ret\n; END METHOD Owner:Value",
        };
        var result = NativeNormalizer.Normalize(listing, [fact], [], [], "X64", [], pointerReturn: true);
        Assert.IsEmpty(result.Problems);
        return result.Lines;
    }
}
