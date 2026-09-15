using IlRepl.Engine;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Instruction, stack, and metadata comparisons use independently compiled original and edited methods.
/// </summary>
[TestClass]
public sealed class MethodEditDiffTests
{
    /// <summary>
    /// An unchanged framework copy compares equal after generated owner identities are normalized.
    /// </summary>
    [TestMethod]
    public void Create_UnchangedFrameworkBodyHasNoNormalizedDifferences()
    {
        var session = new Session();
        var edit = session.PrepareEdit("int32 Math::Max(int32, int32)", "Maximum");
        session.CommitEdit(edit.Name, edit.Source);

        var diff = MethodEditDiff.Create(edit, session);

        Assert.IsFalse(diff.HasChanges, string.Join('\n', diff.Rows.Where(row => row.Kind != "equal")));
        Assert.AreEqual(edit.Name, diff.Name);
        Assert.AreEqual(edit.Fingerprint, diff.BaselineFingerprint);
        Assert.AreEqual(1, diff.Revision);
        Assert.IsFalse(diff.Raw);
        Assert.HasCount(edit.Original.Entries.Count(entry => entry.Instruction is not null), diff.Rows);
        Assert.IsTrue(diff.Rows.All(row => row.Kind == "equal" && row.OriginalStack == row.EditedStack));
        Assert.AreEqual(42, edit.Method!.Invoke(null, [17, 42]));
    }

    /// <summary>
    /// Actual changed constants are reported as removed and added instructions with both resulting stacks.
    /// </summary>
    [TestMethod]
    public void Create_ChangedConstantReportsBothInstructionsAndExecutableResult()
    {
        var session = IlLines.Load(".method int32 Value() { ldc.i4.1; ret }");
        var edit = session.PrepareEdit("Value", "Changed");
        session.CommitEdit(edit.Name, edit.Source.Replace("ldc.i4.1", "ldc.i4.2", StringComparison.Ordinal));

        var diff = MethodEditDiff.Create(edit, session);

        var removed = diff.Rows.Single(row => row.Kind == "removed");
        var added = diff.Rows.Single(row => row.Kind == "added");
        Assert.AreEqual("ldc.i4.1", removed.Original);
        Assert.IsNull(removed.Edited);
        Assert.AreEqual("ldc.i4.2", added.Edited);
        Assert.IsNull(added.Original);
        Assert.Contains("int32", removed.OriginalStack!);
        Assert.Contains("int32", added.EditedStack!);
        Assert.AreEqual(1, edit.OriginalMethod.Invoke(null, null));
        Assert.AreEqual(2, edit.Method!.Invoke(null, null));
        Assert.IsTrue(diff.HasChanges);
    }

    /// <summary>
    /// Equivalent argument, integer, local, and branch encodings compare equal while raw mode retains their byte-level differences.
    /// </summary>
    [TestMethod]
    public void Create_MacroAndBranchEncodingsNormalizeButRawModePreservesThem()
    {
        var session = IlLines.Load(".method int32 Value(int32 x) {", ".locals init (int32 temp)",
            "ldarg.0", "stloc.0", "br.s NEXT", "NEXT: ldloc.0", "ldc.i4.1", "add", "ret", "}");
        var edit = session.PrepareEdit("Value", "Expanded");
        var source = edit.Source.Replace("ldarg.0", "ldarg 0", StringComparison.Ordinal)
            .Replace("stloc.0", "stloc 0", StringComparison.Ordinal)
            .Replace("ldloc.0", "ldloc 0", StringComparison.Ordinal)
            .Replace("br.s ", "br ", StringComparison.Ordinal)
            .Replace("ldc.i4.1", "ldc.i4 1", StringComparison.Ordinal);
        session.CommitEdit(edit.Name, source);

        var normalized = MethodEditDiff.Create(edit, session);
        var raw = MethodEditDiff.Create(edit, session, raw: true);

        Assert.IsFalse(normalized.HasChanges, string.Join('\n', normalized.Rows.Where(row => row.Kind != "equal")));
        Assert.IsTrue(raw.Raw);
        Assert.IsTrue(raw.HasChanges);
        Assert.Contains(row => row.Kind == "removed" && row.Original!.Contains("ldarg.0", StringComparison.Ordinal), raw.Rows);
        Assert.Contains(row => row.Kind == "added" && row.Edited!.Contains("ldarg ", StringComparison.Ordinal), raw.Rows);
        Assert.AreEqual(42, edit.Method!.Invoke(null, [41]));
    }

    /// <summary>
    /// A changed branch destination is retained as a semantic difference and changes the executed return path.
    /// </summary>
    [TestMethod]
    public void Create_ChangedBranchTargetRemainsVisible()
    {
        var session = IlLines.Load(".method int32 Choose() { br FIRST; FIRST: ldc.i4.1; ret; SECOND: ldc.i4.2; ret }");
        var edit = session.PrepareEdit("Choose", "Redirected");
        var branch = edit.Original.Entries.Single(entry => entry.Instruction?.Op.Name == "br").Instruction!;
        var target = edit.Original.Entries.Single(entry => entry.Instruction?.Op.Name == "ldc.i4.2");
        session.CommitEdit(edit.Name, edit.Source.Replace(branch.Text, "br " + IlReader.LabelFor(target.Offset), StringComparison.Ordinal));

        var diff = MethodEditDiff.Create(edit, session);

        Assert.Contains(row => row.Kind == "removed" && row.Original!.StartsWith("br ", StringComparison.Ordinal), diff.Rows);
        Assert.Contains(row => row.Kind == "added" && row.Edited!.StartsWith("br ", StringComparison.Ordinal), diff.Rows);
        Assert.AreEqual(1, edit.OriginalMethod.Invoke(null, null));
        Assert.AreEqual(2, edit.Method!.Invoke(null, null));
    }

    /// <summary>
    /// An unchanged instruction reports independently analyzed stack types when an earlier load changes its input.
    /// </summary>
    [TestMethod]
    public void Create_UnchangedInstructionReportsDifferentInferredStack()
    {
        var session = IlLines.Load(".method object Value() { ldc.i4.1; nop; pop; ldnull; ret }");
        var edit = session.PrepareEdit("Value", "DifferentStack");
        session.CommitEdit(edit.Name, edit.Source.Replace("ldc.i4.1", "ldstr \"value\"", StringComparison.Ordinal));

        var diff = MethodEditDiff.Create(edit, session);

        var changedStack = diff.Rows.Single(row => row.Kind == "stack" && row.Original == "nop");
        Assert.AreEqual("nop", changedStack.Edited);
        Assert.Contains("int32", changedStack.OriginalStack!);
        Assert.Contains("string", changedStack.EditedStack!);
        Assert.IsNull(edit.OriginalMethod.Invoke(null, null));
        Assert.IsNull(edit.Method!.Invoke(null, null));
    }

    /// <summary>
    /// Header settings and local signatures remain visible even when every executable instruction is unchanged.
    /// </summary>
    [TestMethod]
    public void Create_ChangedLocalsInitializationAndMaxStackAreMetadataDifferences()
    {
        var session = IlLines.Load(".method int32 Value() { .maxstack 8; .locals init (int32 spare); ldc.i4 42; ret }");
        var edit = session.PrepareEdit("Value", "Metadata");
        var source = edit.Source.Replace(".maxstack 8", ".maxstack 32", StringComparison.Ordinal)
            .Replace(".locals init (int32 V_0)", ".locals (int64 V_0)", StringComparison.Ordinal);
        session.CommitEdit(edit.Name, source);

        var diff = MethodEditDiff.Create(edit, session);

        var metadata = diff.Rows.Where(row => row.Kind == "metadata").ToArray();
        Assert.HasCount(3, metadata);
        Assert.Contains(row => row.Original == ".maxstack 8" && row.Edited == ".maxstack 32", metadata);
        Assert.Contains(row => row.Original == ".initlocals True" && row.Edited == ".initlocals False", metadata);
        Assert.Contains(row => row.Original == ".locals (int32)" && row.Edited == ".locals (int64)", metadata);
        Assert.DoesNotContain(row => row.Kind is "added" or "removed", diff.Rows);
        Assert.AreEqual(42, edit.Method!.Invoke(null, null));
    }

    /// <summary>
    /// Exception clause order is part of the comparison because the first applicable handler determines dispatch.
    /// </summary>
    [TestMethod]
    public void Create_ChangedExceptionClauseOrderChangesMetadataAndDispatch()
    {
        var session = IlLines.Load(
            ".method int32 CatchOrder() {",
            ".locals init (int32 result)",
            ".try START to END catch System.Exception handler FIRST to FIRST_END",
            ".try START to END catch System.Exception handler SECOND to SECOND_END",
            "START: newobj instance void System.Exception::.ctor()",
            "throw",
            "END:",
            "FIRST: pop", "ldc.i4 11", "stloc.0", "leave DONE", "FIRST_END:",
            "SECOND: pop", "ldc.i4 22", "stloc.0", "leave DONE", "SECOND_END:",
            "DONE: ldloc.0", "ret", "}");
        var edit = session.PrepareEdit("CatchOrder", "Reordered");
        var lines = edit.Source.Split('\n');
        var clauses = Enumerable.Range(0, lines.Length).Where(index => lines[index].StartsWith(".try ",
            StringComparison.Ordinal)).ToArray();
        Assert.HasCount(2, clauses);
        (lines[clauses[0]], lines[clauses[1]]) = (lines[clauses[1]], lines[clauses[0]]);
        session.CommitEdit(edit.Name, string.Join('\n', lines));

        var diff = MethodEditDiff.Create(edit, session);

        Assert.HasCount(2, diff.Rows.Where(row => row.Kind == "metadata" && row.Original!.StartsWith(".try ", StringComparison.Ordinal)));
        Assert.DoesNotContain(row => row.Kind is "added" or "removed", diff.Rows);
        Assert.AreEqual(11, edit.OriginalMethod.Invoke(null, null));
        Assert.AreEqual(22, edit.Method!.Invoke(null, null));
    }

    /// <summary>
    /// A prepared draft has no executed revision to compare and reports the missing commit explicitly.
    /// </summary>
    [TestMethod]
    public void Create_UncommittedDraftReportsMissingRevision()
    {
        var session = IlLines.Load(".method int32 Value() { ldc.i4.1; ret }");
        var edit = session.PrepareEdit("Value", "Pending");

        var error = Assert.ThrowsExactly<ReplException>(() => MethodEditDiff.Create(edit, session));

        Assert.Contains("has no committed version", error.Message);
        Assert.IsNull(edit.Method);
        Assert.AreEqual(0, edit.Revision);
    }
}
