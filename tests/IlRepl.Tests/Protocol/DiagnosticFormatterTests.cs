using IlRepl.Protocol;

namespace IlRepl.Tests.Protocol;

/// <summary>
/// Checks complete evidence rendering, missing provenance, and source identity distinctions.
/// </summary>
[TestClass]
public sealed class DiagnosticFormatterTests
{
    /// <summary>
    /// Every stack state is rendered distinctly and established facts are retained on a partially unknown path.
    /// </summary>
    /// <param name="kind">The incoming analysis state.</param>
    /// <param name="expected">The expected rendered stack description.</param>
    [TestMethod]
    [DataRow(AnalyzedStackKind.Known, "[string]")]
    [DataRow(AnalyzedStackKind.Unknown, "unknown (established path: [string])")]
    [DataRow(AnalyzedStackKind.Invalid, "invalid")]
    [DataRow(AnalyzedStackKind.Unreachable, "unreachable")]
    public void StackStates_RetainTheirMeaning(AnalyzedStackKind kind, string expected)
    {
        var diagnostic = new AnalysisDiagnostic("FLOW005", AnalysisDiagnosticKind.Error, "wrong type",
            new("document", 1, 0, 4), [])
        {
            Explanation = new(null, "int32", new(kind, ["string"], true), [], []),
        };
        var lines = DiagnosticFormatter.Details(diagnostic).ToArray();
        Assert.AreSequenceEqual(["Expected: int32", "Stack before (bottom → top): " + expected + " (incomplete body)"], lines);
        Assert.DoesNotContain("wrong type", lines);
    }

    /// <summary>
    /// A value with unavailable provenance is identified honestly and never given a fabricated source line.
    /// </summary>
    [TestMethod]
    public void MissingProducer_IsExplicitWithoutInventingALocation()
    {
        var diagnostic = new AnalysisDiagnostic("FLOW005", AnalysisDiagnosticKind.Error, "wrong type",
            new("document", 0, 0, 4), [])
        {
            Explanation = new(null, "int32", new(AnalyzedStackKind.Known, ["string"]),
                [new(0, "argument 1", "int32", "string", [])], []),
        };
        var output = string.Join('\n', DiagnosticFormatter.Details(diagnostic));
        Assert.Contains("argument 1: expected int32; actual string", output);
        Assert.Contains("producer unavailable", output);
        Assert.DoesNotContain("line ", output);
        Assert.AreEqual("source location unavailable", DiagnosticFormatter.Location(new("unknown", -1, 0, 0)));
    }

    /// <summary>
    /// Document, accepted, imported, and synthetic producers keep distinct source descriptions.
    /// </summary>
    [TestMethod]
    public void SourceKinds_DoNotConfusePreviousSourceWithTheEditor()
    {
        var location = new AnalysisLocation("PreviousMethod", 2, 0, 3);
        Assert.AreEqual("line 3: nop", DiagnosticFormatter.Source(new(location, "nop", AnalysisSourceKind.Document)));
        Assert.AreEqual("accepted line 3: nop", DiagnosticFormatter.Source(new(location, "nop", AnalysisSourceKind.Accepted)));
        Assert.AreEqual("PreviousMethod IL_001a: nop",
            DiagnosticFormatter.Source(new(location with { Offset = 26 }, "nop", AnalysisSourceKind.Imported)));
        Assert.AreEqual("implicit entry: exception",
            DiagnosticFormatter.Source(new(location, "exception", AnalysisSourceKind.Synthetic)));
        Assert.AreEqual("previously accepted source: nop",
            DiagnosticFormatter.Source(new(location with { Line = -1 }, "nop", AnalysisSourceKind.Accepted)));
    }

    /// <summary>
    /// Identical producers are shown once while distinct instructions remain visible even when their source text matches.
    /// </summary>
    [TestMethod]
    public void RepeatedOrigins_DeduplicateWithoutDroppingDistinctProducers()
    {
        var first = new AnalysisSource(new("document", 1, 0, 9), "ldstr \"x\"", AnalysisSourceKind.Document);
        var second = first with { Location = first.Location with { Line = 3 } };
        var diagnostic = new AnalysisDiagnostic("FLOW005", AnalysisDiagnosticKind.Error, "wrong type",
            new("document", 4, 0, 4), [])
        {
            Explanation = new(null, "int32", new(AnalyzedStackKind.Known, ["string"]),
                [new(0, "argument 1", "int32", "string", [first, first, second])], []),
        };
        var origins = DiagnosticFormatter.Details(diagnostic).Where(line => line.StartsWith("  from ", StringComparison.Ordinal));
        Assert.AreSequenceEqual(["  from line 2: ldstr \"x\"", "  from line 4: ldstr \"x\""], origins);
    }
}
