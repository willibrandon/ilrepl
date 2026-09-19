using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using IlRepl.Engine;
using IlRepl.Protocol;
using IlRepl.Repl;
using IlRepl.Tests.Engine;

namespace IlRepl.Tests.Repl;

/// <summary>
/// Verifies native requests select cells and edits without executing or confusing historical bindings.
/// </summary>
[TestClass]
public sealed class NativeReplTests
{
    /// <summary>
    /// Supplies cancellation to the real in-process engine ticket lifecycle.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// A bare native command selects pending source even when the preceding disassembly selected a named method.
    /// </summary>
    [TestMethod]
    public void Jit_BareCommandSelectsPendingCellInsteadOfLastDisassembly()
    {
        using var files = new SessionWorkspaceFixture();
        using var core = new ReplCore();
        Submit(core, ".method int32 Other() { ldc.i4.7; ret }", ".dis Other");
        foreach (var line in files.PendingDocument().Entries.SelectMany(entry => entry.Source))
        {
            Submit(core, line);
        }

        var before = core.Status;

        var result = core.Handle(".jit");

        Assert.IsTrue(result.Succeeded, Plain(core));
        Assert.IsNotNull(result.NativePackage);
        Assert.AreEqual("current cell", result.NativePackage.Left.Name);
        Assert.IsNull(result.NativePackage.Left.Method);
        Assert.IsNotNull(result.NativePackage.Left.Cell);
        Assert.AreSequenceEqual(files.PendingDocument().Entries.SelectMany(entry => entry.Source).ToArray(),
            result.NativePackage.Left.Cell.Body);
        Assert.IsFalse(result.NativePackage.Options.Run);
        Assert.IsFalse(File.Exists(files.MarkerPath));
        Assert.AreEqual(before.Instructions, core.Status.Instructions);
        Assert.AreEqual(before.CellNumber, core.Status.CellNumber);
    }

    /// <summary>
    /// When no executable cell exists, bare native inspection never falls back to the last disassembled method.
    /// </summary>
    [TestMethod]
    public void Jit_NoExecutableCellDoesNotReuseDisassemblyTarget()
    {
        using var core = new ReplCore();
        Submit(core, ".method int32 Other() { ldc.i4.7; ret }", ".dis Other");

        var result = core.Handle(".jit");

        Assert.IsFalse(result.Succeeded);
        Assert.IsNull(result.NativePackage);
        Assert.Contains("there is no retained executable cell", Plain(core));
        Assert.HasCount(1, core.Session.Methods);
        Assert.IsTrue(core.Status.CellIsEmpty);
    }

    /// <summary>
    /// The latest executed cell can be reconstructed after later declarations without repeating its prior side effects.
    /// </summary>
    /// <param name="explicitNumber">Whether the command uses the cell's explicit historical number.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Jit_HistoricalCellReconstructsWithoutExecuting(bool explicitNumber)
    {
        using var files = new SessionWorkspaceFixture();
        using var core = new ReplCore();
        foreach (var line in files.PendingDocument().Entries.SelectMany(entry => entry.Source))
        {
            Submit(core, line);
        }

        var number = core.Status.CellNumber;
        Submit(core, "ret");
        Assert.AreEqual("executed", File.ReadAllText(files.MarkerPath));
        File.Delete(files.MarkerPath);
        Submit(core, ".method int32 Later() { ldc.i4.7; ret }", ".dis Later");
        var before = core.Status;

        var result = core.Handle(explicitNumber ? ".jit cell " + number : ".jit");

        Assert.IsTrue(result.Succeeded, Plain(core));
        Assert.IsNotNull(result.NativePackage);
        Assert.AreEqual("cell " + number, result.NativePackage.Left.Name);
        Assert.IsNotNull(result.NativePackage.Left.Cell);
        Assert.Contains("ldc.i4.s 42", result.NativePackage.Left.Cell.Body);
        Assert.DoesNotContain(binding => binding.Name == "Later", result.NativePackage.Left.Bindings);
        Assert.IsFalse(File.Exists(files.MarkerPath));
        Assert.IsTrue(core.Status.CellIsEmpty);
        Assert.AreEqual(before.CellNumber, core.Status.CellNumber);
    }

    /// <summary>
    /// Historical native capture retains the dependency implementation used by that cell rather than a later redefinition.
    /// </summary>
    [TestMethod]
    public void Jit_HistoricalCellRetainsEarlierMethodBinding()
    {
        using var core = new ReplCore();
        Submit(core, ".method int32 Value() { ldc.i4.s 42; ret }", "call Value");
        var number = core.Status.CellNumber;
        var pending = core.Handle(".jit");
        Assert.IsNotNull(pending.NativePackage);
        var originalBody = core.Session.Methods.Single().Version.Body.GetMethodBody()!.GetILAsByteArray()!;
        Submit(core, "ret", ".method int32 Value() { ldc.i4.s 43; ret }");

        var historical = core.Handle(".jit cell " + number);

        Assert.IsTrue(historical.Succeeded, Plain(core));
        Assert.IsNotNull(historical.NativePackage);
        var binding = historical.NativePackage.Left.Bindings.Single(item => item.Name == "Value");
        var image = historical.NativePackage.Left.Assemblies.Single(item => item.Name == binding.Implementation.Assembly);
        using var stream = new MemoryStream(image.Image, writable: false);
        using var reader = new PEReader(stream);
        var metadata = reader.GetMetadataReader();
        var method = metadata.GetMethodDefinition((MethodDefinitionHandle)MetadataTokens.Handle(binding.Implementation.Token));
        var historicalBody = reader.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()!;
        var currentBody = NativeCapture.Resolve(core.Session, "Value").GetMethodBody()!.GetILAsByteArray()!;
        Assert.AreSequenceEqual(originalBody, historicalBody);
        Assert.AreNotEqual(SessionCodec.Hash(currentBody), SessionCodec.Hash(historicalBody));
    }

    /// <summary>
    /// Native edit comparisons capture their original and committed sides and preserve assertion mode.
    /// </summary>
    /// <param name="separator">The whitespace separating the selector and native flags.</param>
    [TestMethod]
    [DataRow(" ")]
    [DataRow("\t")]
    public void DiffNative_UsesSharedComparisonCaptureWithOriginalAndEditedSides(string separator)
    {
        using var core = new ReplCore();
        Submit(core, ".method int32 Value() { ldc.i4.1; ret }",
            ".edit Value as Copy {", ".method public static int32 Value() cil managed {", "ldc.i4.2", "ret", "}", "}");

        var result = core.Handle(".diff Copy" + separator + "--native" + separator + "--assert");

        Assert.IsTrue(result.Succeeded, Plain(core));
        Assert.IsNotNull(result.NativePackage);
        Assert.IsTrue(result.NativePackage.Options.Assert);
        Assert.IsTrue(result.NativePackage.Options.Original);
        Assert.AreEqual("Copy", result.NativePackage.Options.Selector);
        Assert.AreEqual("Copy", result.NativePackage.Options.Against);
        Assert.IsNotNull(result.NativePackage.Right);
        Assert.AreEqual("Copy (original)", result.NativePackage.Left.Name);
        Assert.AreEqual("Copy (edited)", result.NativePackage.Right.Name);
        Assert.AreNotEqual(result.NativePackage.Left.Fingerprint, result.NativePackage.Right.Fingerprint);
        Assert.IsFalse(result.NativePackage.Options.Run);
    }

    /// <summary>
    /// Arbitrary comparisons distinguish identical selectors by side and standalone original inspection retains its origin label.
    /// </summary>
    [TestMethod]
    public void Jit_LabelsArbitrarySidesAndStandaloneOriginal()
    {
        using var core = new ReplCore();
        Submit(core, ".method int32 Read() { ldc.i4.1; ret }", ".edit Read as Copy {",
            ".method public static int32 Read() cil managed {", "ldc.i4.2", "ret", "}", "}");

        var comparison = core.Handle(".jit Read --against Read");
        var original = core.Handle(".jit Copy --original");

        Assert.IsTrue(comparison.Succeeded, Plain(core));
        Assert.IsNotNull(comparison.NativePackage);
        Assert.IsNotNull(comparison.NativePackage.Right);
        Assert.AreEqual("left: Read", comparison.NativePackage.Left.Name);
        Assert.AreEqual("right: Read", comparison.NativePackage.Right.Name);
        Assert.AreEqual(comparison.NativePackage.Left.Fingerprint, comparison.NativePackage.Right.Fingerprint);
        Assert.IsTrue(original.Succeeded, Plain(core));
        Assert.IsNotNull(original.NativePackage);
        Assert.AreEqual("Copy (original)", original.NativePackage.Left.Name);
        Assert.IsNull(original.NativePackage.Right);
    }

    /// <summary>
    /// A native-looking token inside a quoted workload literal cannot select the native comparison grammar.
    /// </summary>
    [TestMethod]
    public void Diff_QuotedNativeLiteralDoesNotSelectNativeComparison()
    {
        using var core = new ReplCore();
        Submit(core, ".method int32 Value() { ldc.i4.1; ret }", ".edit Value as Copy {",
            ".method public static int32 Value() cil managed {", "ldc.i4.2", "ret", "}", "}");

        var result = core.Handle(".diff Copy (\" --native \")");

        Assert.IsFalse(result.Succeeded);
        Assert.IsNull(result.NativePackage);
        Assert.Contains("usage: .diff [Name] [--raw]", Plain(core));
        Assert.IsTrue(core.Status.CellIsEmpty);
    }

    /// <summary>
    /// An unavailable in-process worker gives an actionable incomplete report and consumes its ticket exactly once.
    /// </summary>
    [TestMethod]
    public async Task InspectNative_UnavailableWorkerConsumesTicketWithoutExecuting()
    {
        using var files = new SessionWorkspaceFixture();
        await using var engine = new InProcessEngine();
        var cancellationToken = TestContext.CancellationToken;
        foreach (var line in files.PendingDocument().Entries.SelectMany(entry => entry.Source))
        {
            Assert.IsTrue((await engine.HandleAsync(line, cancellationToken)).Succeeded);
        }

        var prepared = await engine.HandleAsync(".jit", cancellationToken);
        Assert.IsNotNull(prepared.PendingNative);

        var result = await engine.InspectNativeAsync(prepared.PendingNative.Identity, cancellationToken);

        Assert.IsFalse(result.Succeeded);
        Assert.IsNotNull(result.Native);
        Assert.AreEqual("incomplete", result.Native.Outcome);
        Assert.Contains("no isolated CoreCLR native inspection worker", result.Native.Left.Detail!);
        Assert.Contains("  current cell: incomplete; noncollectible; 0 workload invocations",
            result.Lines.Select(line => line.PlainText));
        Assert.IsFalse(File.Exists(files.MarkerPath));
        Assert.IsFalse(engine.Status.CellIsEmpty);
        await Assert.ThrowsExactlyAsync<ReplEngineException>(() =>
            engine.InspectNativeAsync(prepared.PendingNative.Identity, cancellationToken));
    }

    /// <summary>
    /// Resetting between request preparation and inspection rejects the stale snapshot without running it.
    /// </summary>
    [TestMethod]
    public async Task InspectNative_ResetRejectsPreparedTicket()
    {
        await using var engine = new InProcessEngine();
        var cancellationToken = TestContext.CancellationToken;
        Assert.IsTrue((await engine.HandleAsync("ldc.i4.s 42", cancellationToken)).Succeeded);
        var prepared = await engine.HandleAsync(".jit", cancellationToken);
        Assert.IsNotNull(prepared.PendingNative);
        Assert.IsTrue((await engine.HandleAsync(".reset", cancellationToken)).Succeeded);

        await Assert.ThrowsExactlyAsync<ReplEngineException>(() =>
            engine.InspectNativeAsync(prepared.PendingNative.Identity, cancellationToken));

        Assert.IsTrue(engine.Status.CellIsEmpty);
        Assert.IsTrue((await engine.HandleAsync("ldc.i4.7", cancellationToken)).Succeeded);
        var run = await engine.HandleAsync("ret", cancellationToken);
        Assert.IsTrue(run.Succeeded);
        Assert.Contains(line => line.Kind == LineKind.Result
            && line.PlainText.Contains("= 7 : int32", StringComparison.Ordinal), run.Lines);
    }

    private static string Plain(ReplCore core) => string.Join('\n', core.Transcript.Lines.Select(line => line.PlainText));

    private static void Submit(ReplCore core, params string[] source)
    {
        foreach (var line in IlLines.Expand(source))
        {
            Assert.IsTrue(core.Handle(line).Succeeded, line + "\n" + Plain(core));
        }
    }
}
