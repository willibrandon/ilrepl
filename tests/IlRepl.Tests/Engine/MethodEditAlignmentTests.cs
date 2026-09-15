using IlRepl.Engine;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Normalized differences retain control-flow destinations and constructed generic identities across shifted instructions.
/// </summary>
[TestClass]
public sealed class MethodEditAlignmentTests
{
    /// <summary>
    /// Large changed bodies retain every addition and removal when comparison switches to bounded memory.
    /// </summary>
    [TestMethod]
    public void Diff_LargeRewriteRetainsAllInstructionChanges()
    {
        var session = new Session();
        session.AddLine(".method int32 Value() {");
        for (var index = 0; index < 300; index++)
        {
            session.AddLine("ldc.i4 " + (1000 + index));
            session.AddLine("pop");
        }

        session.AddLine("ldc.i4.s 42");
        session.AddLine("ret");
        session.AddLine("}");
        var edit = session.PrepareEdit("Value", "Copy");
        var source = edit.Source;
        for (var index = 0; index < 300; index++)
        {
            source = source.Replace("ldc.i4 " + (1000 + index), "ldc.i4 " + (2000 + index), StringComparison.Ordinal);
        }

        session.CommitEdit(edit.Name, source);
        var diff = MethodEditDiff.Create(edit, session);

        Assert.HasCount(300, diff.Rows.Where(row => row.Kind == "added"));
        Assert.HasCount(300, diff.Rows.Where(row => row.Kind == "removed"));
        Assert.HasCount(302, diff.Rows.Where(row => row.Kind == "equal"));
        Assert.AreEqual(42, edit.OriginalMethod.Invoke(null, null));
        Assert.AreEqual(42, edit.Method!.Invoke(null, null));
    }

    /// <summary>
    /// Inserting an instruction before a retained destination does not manufacture a changed branch.
    /// </summary>
    [TestMethod]
    public void Diff_InsertedInstructionPreservesBranchDestination()
    {
        var session = IlLines.Load(".method int32 Value() { br NEXT; NEXT: ldc.i4.s 42; ret }");
        var edit = session.PrepareEdit("Value", "Copy");
        var branch = edit.Original.Entries.Single(entry => entry.Instruction?.Op.Name == "br").Instruction!;
        session.CommitEdit(edit.Name, edit.Source.Replace(branch.Text, branch.Text + "\nnop", StringComparison.Ordinal));

        var diff = MethodEditDiff.Create(edit, session);

        Assert.Contains(row => row.Kind == "added" && row.Edited == "nop", diff.Rows);
        Assert.DoesNotContain(row => row.Kind == "removed", diff.Rows);
        Assert.Contains(row => row.Kind == "equal" && row.Original == branch.Text, diff.Rows);
        Assert.AreEqual(42, edit.Method!.Invoke(null, null));
    }

    /// <summary>
    /// Calls on distinct closed owners stay different even when their method definition token is identical.
    /// </summary>
    [TestMethod]
    public void Diff_ChangedGenericOwnerArgumentsRemainVisible()
    {
        var session = IlLines.Load(".class public Box<T> {",
            ".method public static object Default() { .locals init (!T value); ldloc.0; box !T; ret }", "}",
            ".method object Value() { call object class Box<int32>::Default(); ret }");
        var edit = session.PrepareEdit("Value", "Copy");
        session.CommitEdit(edit.Name, edit.Source.Replace("<int32>", "<int64>", StringComparison.Ordinal));

        var diff = MethodEditDiff.Create(edit, session);

        Assert.Contains(row => row.Kind == "removed" && row.Original!.StartsWith("call ", StringComparison.Ordinal), diff.Rows);
        Assert.Contains(row => row.Kind == "added" && row.Edited!.StartsWith("call ", StringComparison.Ordinal), diff.Rows);
        Assert.IsInstanceOfType<int>(edit.OriginalMethod.Invoke(null, null));
        Assert.IsInstanceOfType<long>(edit.Method!.Invoke(null, null));
    }
}
