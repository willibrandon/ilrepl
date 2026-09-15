using IlRepl.Engine;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Complete method parsing validates branch joins and preserves the last committed copy after an invalid revision.
/// </summary>
[TestClass]
public sealed class MethodEditFlowTests
{
    /// <summary>
    /// A type mismatch discovered at a forward branch join cannot replace an executable committed copy.
    /// </summary>
    [TestMethod]
    public void Commit_InvalidForwardJoinPreservesThePreviousRevision()
    {
        var session = IlLines.Load(".method int32 Read(bool flag) {", "ldarg.0", "brtrue RIGHT", "ldc.i4.s 42", "br JOIN",
            "RIGHT: ldc.i4.s 43", "JOIN: ret", "}");
        var edit = session.PrepareEdit("Read", "Copy");
        session.CommitEdit(edit.Name, edit.Source);
        var previous = edit.Method;
        Assert.AreEqual(42, previous!.Invoke(null, [false]));
        Assert.AreEqual(43, previous.Invoke(null, [true]));
        var invalid = """
            .method public static int32 Read(bool flag) {
              ldarg.0
              brtrue RIGHT
              ldc.i4.s 42
              br JOIN
            RIGHT: ldstr "incompatible result"
            JOIN: ret
            }
            """;
        Assert.ThrowsExactly<ReplException>(() => session.CommitEdit(edit.Name, invalid));
        Assert.AreSame(previous, edit.Method);
        Assert.AreEqual(1, edit.Revision);
        Assert.AreEqual(42, edit.Method!.Invoke(null, [false]));
        Assert.AreEqual(43, edit.Method.Invoke(null, [true]));
    }

    /// <summary>
    /// A complete replacement without an explicit return retains its final stack value for emission.
    /// </summary>
    [TestMethod]
    public void Commit_ImplicitReturnUsesTheCompleteBodyStack()
    {
        var session = IlLines.Load(".method int32 Read() { ldc.i4.s 42; ret }");
        var edit = session.PrepareEdit("Read", "Copy");
        session.CommitEdit(edit.Name, ".method public static int32 Read() {\nldc.i4.s 43\n}");
        Assert.AreEqual(43, edit.Method!.Invoke(null, null));
        Assert.AreEqual(42, edit.Original.Requested.Invoke(null, null));
    }
}
