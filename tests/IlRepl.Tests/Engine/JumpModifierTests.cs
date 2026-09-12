using IlRepl.Engine;
using IlRepl.Engine.Binding;
using IlRepl.Protocol;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Cell jumps compare complete argument annotations and the unmodified object return signature.
/// </summary>
[TestClass]
public sealed class JumpModifierTests
{
    private const string Volatile = "[System.Runtime]System.Runtime.CompilerServices.IsVolatile";
    private const string Cdecl = "[System.Runtime]System.Runtime.CompilerServices.CallConvCdecl";

    /// <summary>
    /// Supplies cancellation for speculative analysis.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Cell arguments match a jump target only when their complete modifier order agrees.
    /// </summary>
    /// <param name="matching">Whether both signatures declare their modifiers in the same order.</param>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task CellArguments_CompareExactModifierOrder(bool matching)
    {
        var parameter = $"int32 modreq({Volatile}) modopt({Cdecl})";
        var argument = matching ? parameter : $"int32 modopt({Cdecl}) modreq({Volatile})";
        var session = IlLines.Load(
            $".method object Target({parameter} number) {{ ldarg number; box int32; ret }}",
            $".args ({argument} number = 42)");
        var line = $"jmp object Target({parameter})";
        using var editing = new EditingSession(session);
        var preview = await editing.AnalyzeAsync(new AnalysisRequest([line], 1, 0, 1), TestContext.CancellationToken);

        Assert.AreEqual(!matching, preview.Diagnostics.Any(diagnostic => diagnostic.Kind == AnalysisDiagnosticKind.Error),
            string.Join("; ", preview.Diagnostics.Select(diagnostic => diagnostic.Message)));
        if (matching)
        {
            session.AddLine(line);
            Assert.AreEqual(42, session.Run().Value);
        }
        else
        {
            var error = Assert.ThrowsExactly<ReplException>(() => session.AddLine(line));
            Assert.Contains("jmp target must match", error.Message);
        }
    }

    /// <summary>
    /// A cell cannot jump to an annotated object return because its own return has no modifiers.
    /// </summary>
    /// <param name="modifier">The return annotation, or an empty string for the accepted counterpart.</param>
    [TestMethod]
    [DataRow("")]
    [DataRow("modreq")]
    [DataRow("modopt")]
    public async Task CellReturn_RequiresAnUnmodifiedTarget(string modifier)
    {
        var result = modifier.Length == 0 ? "object" : $"object {modifier}({Volatile})";
        var session = IlLines.Load($".method {result} Target() {{ ldc.i4 42; box int32; ret }}");
        var line = $"jmp {result} Target()";
        using var editing = new EditingSession(session);
        var preview = await editing.AnalyzeAsync(new AnalysisRequest([line], 1, 0, 1), TestContext.CancellationToken);

        Assert.AreEqual(modifier.Length > 0, preview.Diagnostics.Any(diagnostic => diagnostic.Kind == AnalysisDiagnosticKind.Error),
            string.Join("; ", preview.Diagnostics.Select(diagnostic => diagnostic.Message)));
        if (modifier.Length == 0)
        {
            session.AddLine(line);
            Assert.AreEqual(42, session.Run().Value);
        }
        else
        {
            var error = Assert.ThrowsExactly<ReplException>(() => session.AddLine(line));
            Assert.Contains("jmp target must match", error.Message);
        }
    }
}
