using IlRepl.Batch;
using IlRepl.Engine;
using IlRepl.Protocol;

namespace IlRepl.Tests.Protocol;

/// <summary>
/// Method editing and comparison cross real host processes, RPC serialization, and batch exit handling.
/// </summary>
[TestClass]
public sealed class MethodEditingProcessTests
{
    /// <summary>
    /// The cancellation context for process operations.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Dependency provenance crosses real RPC and its readable report follows the latest committed source.
    /// </summary>
    /// <returns>The completed dependency, revision, and serialized-document assertions.</returns>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Rpc_DependencyReportPreservesAccessAndRefreshesAfterRevision()
    {
        var token = TestContext.CancellationToken;
        await using var engine = await HostPaths.StartEngineAsync(token);
        string[] source =
        [
            ".class public Fixture {", ".method private static int32 Helper(int32 value) {", "ldarg.0", "ret", "}",
            ".method public static int32 Read() {", "ldc.i4.s 42", "call int32 Fixture::Helper(int32)", "ldc.i4.1",
            "call int32 Math::Max(int32, int32)", "ret", "}", "}",
        ];
        foreach (var line in source)
        {
            Assert.IsTrue((await engine.HandleAsync(line, token)).Succeeded, line);
        }

        var opened = await engine.HandleAsync(".edit int32 Fixture::Read() as Copy", token);
        Assert.IsNotNull(opened.EditDocument);
        Assert.IsEmpty(opened.EditDocument.Problems);
        var helper = opened.EditDocument.Dependencies.Single(dependency => dependency.Symbol.Contains("Helper", StringComparison.Ordinal));
        Assert.AreEqual("copied", helper.Disposition);
        Assert.AreEqual("private", helper.Access);
        Assert.Contains("Read", helper.Location);
        Assert.Contains("call int32 Fixture::Helper(int32)", helper.Location);
        Assert.Contains("Version=", helper.Assembly);
        var maximum = opened.EditDocument.Dependencies.Single(dependency => dependency.Symbol.Contains("Max(", StringComparison.Ordinal));
        Assert.AreEqual("external", maximum.Disposition);
        Assert.AreEqual("public", maximum.Access);
        Assert.Contains("System.Private.CoreLib", maximum.Assembly);

        var report = await engine.HandleAsync(".methods Copy", token);
        Assert.IsTrue(report.Succeeded);
        var text = string.Join('\n', report.Lines.Select(line => line.PlainText));
        Assert.Contains("copied: private", text);
        Assert.Contains(helper.Symbol, text);
        Assert.Contains(helper.Assembly, text);
        Assert.Contains(helper.Location, text);
        foreach (var line in new[] { ".edit Copy {", ".method public static int32 Read() cil managed {",
            "ldc.i4.7", "ret", "}", "}" })
        {
            Assert.IsTrue((await engine.HandleAsync(line, token)).Succeeded, line);
        }

        var revised = await engine.HandleAsync(".edit Copy", token);
        Assert.IsNotNull(revised.EditDocument);
        Assert.AreEqual(1, revised.EditDocument.Revision);
        Assert.AreEqual(opened.EditDocument.BaselineFingerprint, revised.EditDocument.BaselineFingerprint);
        Assert.IsEmpty(revised.EditDocument.Dependencies);
        var refreshed = await engine.HandleAsync(".methods Copy", token);
        Assert.Contains(line => line.PlainText.Contains("no referenced member dependencies", StringComparison.Ordinal), refreshed.Lines);
        Assert.DoesNotContain(line => line.PlainText.Contains("Helper", StringComparison.Ordinal), refreshed.Lines);
        Assert.IsTrue((await engine.HandleAsync("call Copy", token)).Succeeded);
        var result = await engine.HandleAsync("ret", token);
        Assert.Contains(line => line.Kind == LineKind.Result
            && line.PlainText.Contains("= 7 : int32", StringComparison.Ordinal), result.Lines);
    }

    /// <summary>
    /// A real RPC session returns editable source, structured diffs, and one-use comparison reports.
    /// </summary>
    /// <returns>The completed protocol assertions.</returns>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Rpc_EditDiffAndComparisonPreserveStructuredResults()
    {
        await using var engine = await HostPaths.StartEngineAsync(TestContext.CancellationToken);
        var token = TestContext.CancellationToken;
        foreach (var line in Script(change: true))
        {
            Assert.IsTrue((await engine.HandleAsync(line, token)).Succeeded, line);
        }

        var opened = await engine.HandleAsync(".edit Copy", token);
        Assert.IsNotNull(opened.EditDocument);
        Assert.AreEqual("Copy", opened.EditDocument.Name);
        Assert.AreEqual(1, opened.EditDocument.Revision);
        Assert.Contains(".method", opened.EditDocument.Source);
        var diff = await engine.HandleAsync(".diff Copy", token);
        Assert.IsNotNull(diff.Diff);
        Assert.IsTrue(diff.Diff.HasChanges);
        Assert.Contains(row => row.Kind == "added" && row.Edited == "ldc.i4.2", diff.Diff.Rows);
        var prepared = await engine.HandleAsync(".compare Copy ()", token);
        Assert.IsNotNull(prepared.PendingComparison);
        var report = await engine.CompareAsync(prepared.PendingComparison.Identity, token);
        Assert.IsTrue(report.Succeeded);
        Assert.IsNotNull(report.Comparison);
        Assert.AreEqual("different", report.Comparison.Outcome);
        Assert.AreEqual("1", report.Comparison.Original.Result!.Value);
        Assert.AreEqual("2", report.Comparison.Edited.Result!.Value);
        Assert.AreEqual(opened.EditDocument.BaselineFingerprint, report.Comparison.BaselineFingerprint);
        await Assert.ThrowsAsync<ReplEngineException>(() => engine.CompareAsync(prepared.PendingComparison.Identity, token));
        var next = await engine.HandleAsync(".compare Copy ()", token);
        Assert.IsNotNull(next.PendingComparison);
        Assert.IsTrue((await engine.HandleAsync(".reset", token)).Succeeded);
        await Assert.ThrowsAsync<ReplEngineException>(() => engine.CompareAsync(next.PendingComparison.Identity, token));
        Assert.IsTrue((await engine.HandleAsync("ldc.i4.s 42", token)).Succeeded);
        var result = await engine.HandleAsync("ret", token);
        Assert.Contains(line => line.Kind == LineKind.Result && line.PlainText.Contains("42", StringComparison.Ordinal), result.Lines);
    }

    /// <summary>
    /// Batch assertions fail on differences while ordinary comparisons and complete matches succeed.
    /// </summary>
    /// <param name="change">Whether the edited method changes its return value.</param>
    /// <param name="assert">Whether comparison equality is required by the script.</param>
    /// <param name="exit">The expected batch exit code.</param>
    /// <returns>The completed batch assertions.</returns>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    [DataRow(false, true, 0)]
    [DataRow(true, false, 0)]
    [DataRow(true, true, 1)]
    public async Task Batch_ComparisonAssertionControlsExitCode(bool change, bool assert, int exit)
    {
        await using var engine = await HostPaths.StartEngineAsync(TestContext.CancellationToken);
        using var output = new StringWriter();
        var runner = new BatchRunner(engine, output, color: false, echoInput: false);
        var result = await runner.RunAsync([.. Script(change), ".compare Copy ()" + (assert ? " --assert" : "")],
            TestContext.CancellationToken);
        Assert.AreEqual(exit, result, output.ToString());
        var text = output.ToString();
        Assert.Contains(change ? "Copy: different" : "Copy: match", text);
        Assert.Contains("original: completed", text);
        Assert.Contains("edited: completed", text);
        Assert.IsLessThan(text.IndexOf("Copy: ", StringComparison.Ordinal),
            text.IndexOf("comparison starting state:", StringComparison.Ordinal));
    }

    /// <summary>
    /// Batch source inspection prints the complete document and refuses EOF inside an unfinished edit.
    /// </summary>
    /// <returns>The completed batch assertions.</returns>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Batch_ExposesEditableDocumentAndRejectsIncompleteSubmission()
    {
        await using var engine = await HostPaths.StartEngineAsync(TestContext.CancellationToken);
        using var output = new StringWriter();
        var runner = new BatchRunner(engine, output, color: false, echoInput: false);
        var inspect = await runner.RunAsync([.. Script(false), ".edit Copy"], TestContext.CancellationToken);
        Assert.AreEqual(0, inspect, output.ToString());
        Assert.Contains(".edit Value as Copy {", output.ToString());
        Assert.Contains(".method", output.ToString());
        var incomplete = await runner.RunAsync([".edit Copy {", ".method public static int32 Value() cil managed {"],
            TestContext.CancellationToken);
        Assert.AreEqual(1, incomplete);
        Assert.Contains("edit Copy is still open", output.ToString());
        Assert.AreEqual("Copy", engine.Status.OpenEdit);
        Assert.IsTrue((await engine.HandleAsync(".clear", TestContext.CancellationToken)).Succeeded);
        Assert.IsNull(engine.Status.OpenEdit);
    }

    /// <summary>
    /// Resetting during actual worker execution discards its stale reply and leaves the new session usable.
    /// </summary>
    /// <returns>The completed worker synchronization and RPC assertions.</returns>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Rpc_ResetDuringComparisonRejectsStaleReport()
    {
        var directory = Directory.CreateTempSubdirectory("ilrepl-comparison-reset-");
        var ready = Path.Combine(directory.FullName, "started");
        var release = Path.Combine(directory.FullName, "release");
        try
        {
            var token = TestContext.CancellationToken;
            await using var engine = await HostPaths.StartEngineAsync(token);
            string[] source =
            [
                ".method int32 Wait() {", "ldstr " + LiteralParser.Escape(ready), "ldstr \"started\"",
                "call void File::WriteAllText(string, string)", "WAIT: ldstr " + LiteralParser.Escape(release),
                "call bool File::Exists(string)", "brfalse WAIT", "ldc.i4.s 42", "ret", "}",
            ];
            foreach (var line in source)
            {
                Assert.IsTrue((await engine.HandleAsync(line, token)).Succeeded, line);
            }

            var opened = await engine.HandleAsync(".edit Wait as Copy", token);
            Assert.IsNotNull(opened.EditDocument);
            foreach (var line in opened.EditDocument.Source.Split('\n'))
            {
                Assert.IsTrue((await engine.HandleAsync(line, token)).Succeeded, line);
            }

            var prepared = await engine.HandleAsync(".compare Copy ()", token);
            Assert.IsNotNull(prepared.PendingComparison);
            var comparison = engine.CompareAsync(prepared.PendingComparison.Identity, token);
            while (!File.Exists(ready))
            {
                await Task.Delay(10, token);
            }

            Assert.IsFalse(comparison.IsCompleted);
            Assert.IsTrue((await engine.HandleAsync(".reset", token)).Succeeded);
            await File.WriteAllTextAsync(release, "continue", token);
            var error = await Assert.ThrowsAsync<ReplEngineException>(() => comparison);
            Assert.Contains("revision changed during execution", error.Message);
            Assert.IsTrue((await engine.HandleAsync("ldc.i4.s 42", token)).Succeeded);
            var result = await engine.HandleAsync("ret", token);
            Assert.Contains(line => line.Kind == LineKind.Result && line.PlainText.Contains("42", StringComparison.Ordinal), result.Lines);
            Assert.DoesNotContain(line => line.PlainText.Contains("Copy:", StringComparison.Ordinal), result.Lines);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static string[] Script(bool change) =>
    [
        ".method int32 Value() {", "ldc.i4.1", "ret", "}",
        ".edit Value as Copy {", ".method public static int32 Value() cil managed {",
        change ? "ldc.i4.2" : "ldc.i4.1", "ret", "}", "}",
    ];
}
