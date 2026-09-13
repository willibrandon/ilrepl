using IlRepl.Protocol;
using IlRepl.Repl;
using IlRepl.Tests.Engine;

namespace IlRepl.Tests.Repl;

/// <summary>
/// Edit commands carry executable source through the real REPL, preserve captured originals, and expose structured differences.
/// </summary>
[TestClass]
public sealed class EditCommandTests
{
    /// <summary>
    /// A framework edit document can be replayed verbatim as source and called through its public alias.
    /// </summary>
    [TestMethod]
    public void Handle_EditDocumentRoundTripCommitsAndInvokesCopy()
    {
        var core = new ReplCore();

        var prepared = core.Handle(".edit int32 Math::Max(int32, int32) as Maximum");

        Assert.IsTrue(prepared.Succeeded, Transcript(core));
        Assert.IsNotNull(prepared.EditDocument);
        var document = prepared.EditDocument;
        Assert.AreEqual("Maximum", document.Name);
        Assert.AreEqual("int32 Math::Max(int32, int32)", document.OriginalReference);
        Assert.AreEqual(0, document.Revision);
        Assert.StartsWith(".edit int32 Math::Max(int32, int32) as Maximum {\n.method ", document.Source);
        Assert.StartsWith("edit:Maximum:", document.Identity);
        Assert.IsNull(core.Session.Edits.Single().Method);
        Replay(core, document.Source);

        var committed = core.Session.Edits.Single();
        Assert.AreEqual(document.BaselineFingerprint, committed.Fingerprint);
        Assert.AreEqual(1, committed.Revision);
        Assert.AreEqual(0, core.Status.OpenDepth);
        Assert.IsTrue(core.Handle("ldc.i4 17").Succeeded);
        Assert.IsTrue(core.Handle("ldc.i4 42").Succeeded);
        Assert.IsTrue(core.Handle("call int32 Maximum(int32, int32)").Succeeded, Transcript(core));
        Assert.AreEqual(42, core.Session.Run().Value);
    }

    /// <summary>
    /// The default edit source is the last successfully disassembled method even if a later lookup fails.
    /// </summary>
    [TestMethod]
    public void Handle_EditDefaultsToLastSuccessfulDisassembly()
    {
        var core = new ReplCore();
        Assert.IsTrue(core.Handle(".dis int32 Math::Max(int32, int32)").Succeeded, Transcript(core));
        Assert.IsFalse(core.Handle(".dis int32 Math::Missing(int32)").Succeeded);

        var prepared = core.Handle(".edit");

        Assert.IsTrue(prepared.Succeeded, Transcript(core));
        Assert.IsNotNull(prepared.EditDocument);
        Assert.AreEqual("int32 Math::Max(int32, int32)", prepared.EditDocument.OriginalReference);
        Assert.AreEqual("Max_Edit", prepared.EditDocument.Name);
        Replay(core, prepared.EditDocument.Source);
        Assert.AreEqual(42, core.Session.Edits.Single().Method!.Invoke(null, [42, 0]));
    }

    /// <summary>
    /// The original listing remains captured after both an edit commit and redefinition of the source method.
    /// </summary>
    [TestMethod]
    public void Handle_DisOriginalAndDiffUseCapturedBaseline()
    {
        var core = Load(".method int32 Value() { ldc.i4.1; ret }");
        var prepared = core.Handle(".edit Value as Changed");
        Assert.IsNotNull(prepared.EditDocument);
        Replay(core, prepared.EditDocument.Source.Replace("ldc.i4.1", "ldc.i4.2", StringComparison.Ordinal));
        foreach (var line in IlLines.Expand(".method int32 Value() { ldc.i4.3; ret }"))
        {
            Assert.IsTrue(core.Handle(line).Succeeded, Transcript(core));
        }

        var start = core.Transcript.Lines.Count;
        Assert.IsTrue(core.Handle(".dis Changed --original").Succeeded, Transcript(core));
        var original = string.Join('\n', core.Transcript.Lines.Skip(start).Select(line => line.PlainText));
        var diff = core.Handle(".diff Changed");

        Assert.Contains("ldc.i4.1", original);
        Assert.DoesNotContain("ldc.i4.2", original);
        Assert.DoesNotContain("ldc.i4.3", original);
        Assert.IsTrue(diff.Succeeded, Transcript(core));
        Assert.IsNotNull(diff.Diff);
        Assert.Contains(row => row.Kind == "removed" && row.Original == "ldc.i4.1", diff.Diff.Rows);
        Assert.Contains(row => row.Kind == "added" && row.Edited == "ldc.i4.2", diff.Diff.Rows);
        Assert.AreEqual(2, core.Session.Edits.Single().Method!.Invoke(null, null));
        Assert.AreEqual(3, core.Session.Methods.Single().Version.Body.Invoke(null, null));
    }

    /// <summary>
    /// Diff defaults to the latest edit and supports an explicit older edit and raw encodings.
    /// </summary>
    [TestMethod]
    public void Handle_DiffDefaultAndRawOptionsSelectRequestedEdit()
    {
        var core = Load(".method int32 Value() { ldc.i4.1; ret }");
        var first = Prepare(core, ".edit Value as First");
        Replay(core, first.Source);
        var second = Prepare(core, ".edit Value as Second");
        Replay(core, second.Source.Replace("ldc.i4.1", "ldc.i4 1", StringComparison.Ordinal));

        var latest = core.Handle(".diff");
        var earlier = core.Handle(".diff First");
        var raw = core.Handle(".diff Second --raw");

        Assert.IsTrue(latest.Succeeded, Transcript(core));
        Assert.IsNotNull(latest.Diff);
        Assert.AreEqual("Second", latest.Diff.Name);
        Assert.IsFalse(latest.Diff.HasChanges);
        Assert.IsTrue(earlier.Succeeded, Transcript(core));
        Assert.AreEqual("First", earlier.Diff!.Name);
        Assert.IsTrue(raw.Succeeded, Transcript(core));
        Assert.IsTrue(raw.Diff!.Raw);
        Assert.IsTrue(raw.Diff.HasChanges);
        Assert.Contains("no instruction, stack, or metadata differences", Transcript(core));
    }

    /// <summary>
    /// A failed closing commit preserves the callable revision and clearing its draft allows a corrected revision to be submitted.
    /// </summary>
    [TestMethod]
    public void Handle_InvalidEditCanBeAbandonedAndCorrectedWithoutLosingCopy()
    {
        var core = Load(".method int32 Value() { ldc.i4.1; ret }");
        var prepared = Prepare(core, ".edit Value as Changed");
        Replay(core, prepared.Source);
        var firstMethod = core.Session.Edits.Single().Method;
        var bad = Prepare(core, ".edit Changed");
        var lines = bad.Source.Replace("ldc.i4.1", "nop", StringComparison.Ordinal).Split('\n');
        foreach (var line in lines[..^1])
        {
            Assert.IsTrue(core.Handle(line).Succeeded, Transcript(core));
        }

        var rejected = core.Handle(lines[^1]);

        Assert.IsFalse(rejected.Succeeded);
        Assert.AreSame(firstMethod, core.Session.Edits.Single().Method);
        Assert.AreEqual(1, firstMethod!.Invoke(null, null));
        Assert.AreEqual(1, core.Session.Edits.Single().Revision);
        Assert.AreEqual(1, core.Status.OpenDepth);
        Assert.IsTrue(core.Handle(".clear").Succeeded);
        Assert.AreEqual(0, core.Status.OpenDepth);
        var reopened = Prepare(core, ".edit Changed");
        Assert.AreEqual(prepared.Identity, reopened.Identity);
        Assert.AreEqual(1, reopened.Revision);
        Replay(core, reopened.Source.Replace("ldc.i4.1", "ldc.i4.2", StringComparison.Ordinal));
        Assert.AreEqual(2, core.Session.Edits.Single().Revision);
        Assert.AreEqual(2, core.Session.Edits.Single().Method!.Invoke(null, null));
        Assert.AreEqual(1, core.Session.Edits.Single().OriginalMethod.Invoke(null, null));
    }

    /// <summary>
    /// Braces inside string literals and comments do not close the outer edit document.
    /// </summary>
    [TestMethod]
    public void Handle_EditDocumentIgnoresLiteralAndCommentBraces()
    {
        var core = Load(".method string Value() { ldstr \"initial\"; ret }");
        var document = Prepare(core, ".edit Value as Braces");

        Replay(core, document.Source.Replace("ldstr \"initial\"", "ldstr \"{ result }\" // }", StringComparison.Ordinal));

        Assert.AreEqual("{ result }", core.Session.Edits.Single().Method!.Invoke(null, null));
        Assert.AreEqual(0, core.Status.OpenDepth);
    }

    /// <summary>
    /// Missing defaults and uncommitted comparisons produce actionable command errors without creating executable copies.
    /// </summary>
    [TestMethod]
    public void Handle_MissingEditAndUncommittedDiffReportErrors()
    {
        var core = new ReplCore();
        Assert.IsFalse(core.Handle(".edit").Succeeded);
        Assert.Contains("disassemble a method first", Transcript(core));
        Assert.IsFalse(core.Handle(".diff").Succeeded);
        Assert.Contains("no matching edit", Transcript(core));
        var prepared = core.Handle(".edit int32 Math::Max(int32, int32) as Pending");
        Assert.IsTrue(prepared.Succeeded, Transcript(core));

        Assert.IsFalse(core.Handle(".diff Pending").Succeeded);

        Assert.Contains("has no committed version", Transcript(core));
        Assert.IsNull(core.Session.Edits.Single().Method);
        Assert.AreEqual(0, core.Session.Edits.Single().Revision);
    }

    private static EditDocument Prepare(ReplCore core, string command)
    {
        var result = core.Handle(command);
        Assert.IsTrue(result.Succeeded, Transcript(core));
        Assert.IsNotNull(result.EditDocument);
        return result.EditDocument;
    }

    private static ReplCore Load(string source)
    {
        var core = new ReplCore();
        foreach (var line in IlLines.Expand(source))
        {
            Assert.IsTrue(core.Handle(line).Succeeded, Transcript(core));
        }

        return core;
    }

    private static void Replay(ReplCore core, string source)
    {
        foreach (var line in source.Split('\n'))
        {
            Assert.IsTrue(core.Handle(line).Succeeded, line + "\n" + Transcript(core));
        }
    }

    private static string Transcript(ReplCore core) => string.Join('\n', core.Transcript.Lines.Select(line => line.PlainText));
}
