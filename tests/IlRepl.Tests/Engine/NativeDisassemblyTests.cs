using IlRepl.Engine;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Verifies release JIT blocks preserve tier provenance and operands while excluding incomplete compilations.
/// </summary>
[TestClass]
public sealed class NativeDisassemblyTests
{
    /// <summary>
    /// Ordered complete code versions retain their runtime tier names while an unfinished trailing version is excluded.
    /// </summary>
    [TestMethod]
    public void Parse_CompleteVersionsKeepOrderAndExcludeIncompleteTail()
    {
        const string partial = "; Assembly listing for method Owner:Value():int (FullOpts)\n; BEGIN METHOD Owner:Value\nmov eax, 44\n";
        var text = Listing("Instrumented Tier0", "optimized using Dynamic PGO", "       B82A000000 mov eax, 42")
            + "\nJIT summary: unrelated method\n"
            + Listing("Tier1-OSR", "optimized using Dynamic PGO", "       B82B000000 mov eax, 43")
            + "\n" + partial;

        var compilations = NativeDisassembly.Parse(text.Replace("\n", "\r\n", StringComparison.Ordinal));

        Assert.HasCount(2, compilations);
        Assert.AreEqual("Owner:Value():int", compilations[0].Method);
        Assert.AreEqual("Instrumented Tier0", compilations[0].Tier);
        Assert.AreEqual("Tier1-OSR", compilations[1].Tier);
        Assert.AreEqual("Dynamic", compilations[0].Pgo);
        Assert.AreEqual(6, compilations[0].CodeSize);
        Assert.Contains("mov eax, 42", compilations[0].Listing);
        Assert.Contains("mov eax, 43", compilations[1].Listing);
        Assert.DoesNotContain("mov eax, 44", string.Join('\n', compilations.Select(item => item.Listing)));
        Assert.AreEqual(partial, Assert.ContainsSingle(NativeDisassembly.Unattributed(text, ["Owner:Value():int"], compilations)));
    }

    /// <summary>
    /// Only selected concrete and canonical signatures retain unproven code while unrelated overloads stay excluded.
    /// </summary>
    [TestMethod]
    public void Unattributed_PreservesOnlySelectedConcreteAndCanonicalBlocks()
    {
        const string concrete = "Owner:Value[System.String](System.String):System.String";
        const string canonical = "Owner:Value[System.__Canon](System.__Canon):System.__Canon";
        var ordinary = Listing("FullOpts", "No PGO data", "       C3 ret");
        var selected = ordinary.Replace("Owner:Value():int", concrete, StringComparison.Ordinal);
        var shared = ordinary.Replace("Owner:Value():int", canonical, StringComparison.Ordinal);
        var overload = ordinary.Replace("Owner:Value():int", "Owner:Value(int):int", StringComparison.Ordinal);
        var text = selected + overload + shared + ordinary;
        var attributed = NativeDisassembly.Parse(selected);

        var unattributed = NativeDisassembly.Unattributed(text, [concrete, canonical], attributed);

        Assert.AreEqual(shared.TrimEnd('\n'), Assert.ContainsSingle(unattributed));
        Assert.AreEqual(concrete, Assert.ContainsSingle(attributed).Method);
        Assert.DoesNotContain("Owner:Value(int):int", string.Join('\n', unattributed));
        Assert.DoesNotContain("Owner:Value():int", string.Join('\n', unattributed));
    }

    /// <summary>
    /// Identical output blocks require one publication attribution per occurrence instead of a set membership match.
    /// </summary>
    /// <param name="publishedCount">How many identical blocks have publication evidence.</param>
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public void Unattributed_ConsumesEachAttributedOccurrenceOnce(int publishedCount)
    {
        var listing = Listing("FullOpts", "No PGO data", "       B82A000000 mov eax, 42\n       C3 ret");
        var text = listing + listing;
        var compilations = NativeDisassembly.Parse(text);
        Assert.HasCount(2, compilations);

        var unattributed = NativeDisassembly.Unattributed(text, ["Owner:Value():int"], compilations[..publishedCount]);

        Assert.HasCount(2 - publishedCount, unattributed);
        foreach (var block in unattributed)
        {
            Assert.AreEqual(listing.TrimEnd('\n'), block);
        }
    }

    /// <summary>
    /// Profile provenance follows release header evidence without inferring dynamic profiles from an optimized tier.
    /// </summary>
    /// <param name="header">The profile header reported by the runtime.</param>
    /// <param name="expected">The report's observed profile kind.</param>
    [TestMethod]
    [DataRow("optimized using Dynamic PGO", "Dynamic")]
    [DataRow("optimized using Synthesized PGO", "Synthesized")]
    [DataRow("optimized using Static PGO", "Static")]
    [DataRow("No PGO data", "None")]
    [DataRow("optimized code", "Unknown")]
    public void Parse_ProfileKindRequiresHeaderEvidence(string header, string expected)
    {
        var compilation = Assert.ContainsSingle(NativeDisassembly.Parse(Listing("FullOpts", header, "       C3 ret")));

        Assert.AreEqual("FullOpts", compilation.Tier);
        Assert.AreEqual(expected, compilation.Pgo);
    }

    /// <summary>
    /// Initial tier code without a consumed profile reports that profile provenance does not apply.
    /// </summary>
    /// <param name="tier">The initial runtime tier name.</param>
    [TestMethod]
    [DataRow("Tier0")]
    [DataRow("Instrumented Tier0")]
    public void Parse_InitialTierWithoutProfileIsNotApplicable(string tier)
    {
        var compilation = Assert.ContainsSingle(NativeDisassembly.Parse(Listing(tier, "MinOpts code", "       C3 ret")));

        Assert.AreEqual(tier, compilation.Tier);
        Assert.AreEqual("Not applicable", compilation.Pgo);
    }

    /// <summary>
    /// Useful JIT comments remain visible while the complete original header stays available in raw mode.
    /// </summary>
    [TestMethod]
    public void Headers_RetainFrameInterruptibilityAndIsaSeparatelyFromInstructions()
    {
        const string header = "; Assembly listing for method Owner:Value():int (FullOpts)\n"
            + "; Emitting BLENDED_CODE for X64 with VEX\n; rbp based frame\n; fully interruptible\n; No PGO data\n";
        var listing = header + "; BEGIN METHOD Owner:Value\n       C3 ret\n; END METHOD Owner:Value\n"
            + "; Total bytes of code 1, prolog size 0\n";
        var compilation = Assert.ContainsSingle(NativeDisassembly.Parse(listing));

        Assert.AreSequenceEqual(
        [
            "; Emitting BLENDED_CODE for X64 with VEX", "; rbp based frame", "; fully interruptible",
        ], NativeDisassembly.Headers(compilation));
        Assert.AreSequenceEqual(header.TrimEnd('\n').Split('\n'), NativeDisassembly.Headers(compilation, raw: true));
        Assert.AreEqual("ret", Assert.ContainsSingle(NativeDisassembly.Instructions(compilation)).Trim());
        Assert.AreEqual(listing.TrimEnd('\n'), compilation.Listing);
    }

    /// <summary>
    /// Offset comments do not produce normal differences while raw listings preserve their exact encoded positions.
    /// </summary>
    [TestMethod]
    public void Instructions_OffsetOnlyChangesAreEqualUnlessRaw()
    {
        var left = Assert.ContainsSingle(NativeDisassembly.Parse(Listing("FullOpts", "No PGO data",
            "G_M137_IG01:                ;; offset=0x0000\n       B82A000000 mov eax, 42 ;; offset=0x0000\n       C3 ret")));
        var right = Assert.ContainsSingle(NativeDisassembly.Parse(Listing("FullOpts", "No PGO data",
            "G_M222_IG01:                ;; offset=0x0010\n       B82A000000 mov eax, 42 ;; offset=0x0010\n       C3 ret")));

        var normal = NativeDisassembly.Instructions(left);
        Assert.AreSequenceEqual(normal, NativeDisassembly.Instructions(right));
        Assert.Contains("L01:", normal);
        Assert.DoesNotContain(line => line.Contains("offset=", StringComparison.Ordinal), normal);
        Assert.IsEmpty(NativeDifference.Create(normal, NativeDisassembly.Instructions(right), "left", "right"));
        var raw = NativeDisassembly.Instructions(left, raw: true);
        Assert.Contains(line => line.Contains("offset=0x0000", StringComparison.Ordinal), raw);
        Assert.IsNotEmpty(NativeDifference.Create(raw, NativeDisassembly.Instructions(right, raw: true), "left", "right"));
    }

    /// <summary>
    /// A header, code size, and both test anchors are all required before a listing can be treated as complete.
    /// </summary>
    /// <param name="removed">The completion evidence removed from the otherwise complete output.</param>
    [TestMethod]
    [DataRow("; BEGIN METHOD Owner:Value")]
    [DataRow("; END METHOD Owner:Value")]
    [DataRow("; Total bytes of code 6, prolog size 0")]
    public void Parse_MissingCompletionEvidenceDoesNotProduceCompilation(string removed)
    {
        var text = Listing("FullOpts", "No PGO data", "       C3 ret").Replace(removed, "", StringComparison.Ordinal);

        var compilations = NativeDisassembly.Parse(text);

        Assert.IsEmpty(compilations);
    }

    /// <summary>
    /// Ordinary listings hide bytes and unique label prefixes while preserving branch relationships and large constants.
    /// </summary>
    [TestMethod]
    public void Instructions_NormalViewPreservesConstantsAndBranchTargets()
    {
        const string instructions = """
            G_M137_IG01:
                   48B87856341200000000 mov rax, 0x12345678
                   488B8800004000 mov rcx, qword ptr [rax+0x400000]
                   7501 jne G_M137_IG03
            G_M137_IG02:
                   90 nop
            G_M137_IG03:
                   C3 ret
            """;
        var compilation = Assert.ContainsSingle(NativeDisassembly.Parse(Listing("FullOpts", "No PGO data", instructions)));

        var normal = NativeDisassembly.Instructions(compilation);
        var raw = NativeDisassembly.Instructions(compilation, raw: true);

        Assert.Contains("L01:", normal);
        Assert.Contains("L02:", normal);
        Assert.Contains("L03:", normal);
        Assert.Contains(line => line.Contains("jne L03", StringComparison.Ordinal), normal);
        Assert.Contains(line => line.Contains("mov rax, 0x12345678", StringComparison.Ordinal), normal);
        Assert.Contains(line => line.Contains("[rax+0x400000]", StringComparison.Ordinal), normal);
        Assert.DoesNotContain(line => line.Contains("48B87856341200000000", StringComparison.Ordinal), normal);
        Assert.DoesNotContain(line => line.Contains("D1FFAB1E", StringComparison.Ordinal), normal);
        Assert.Contains("G_M137_IG01:", raw);
        Assert.Contains(line => line.Contains("48B87856341200000000", StringComparison.Ordinal), raw);
        Assert.Contains(line => line.Contains("jne G_M137_IG03", StringComparison.Ordinal), raw);
    }

    private static string Listing(string tier, string profile, string instructions) =>
        "; Assembly listing for method Owner:Value():int (" + tier + ")\n; " + profile
        + "\n; BEGIN METHOD Owner:Value\n" + instructions + "\n; END METHOD Owner:Value\n"
        + "; Total bytes of code 6, prolog size 0\n";
}
