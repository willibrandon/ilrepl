using IlRepl.Engine;
using IlRepl.Repl;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Method emission preserves recursive and existing references through activation, rejection, and replacement.
/// </summary>
[TestClass]
public sealed class MethodReferenceBindingTests
{
    /// <summary>
    /// A retained recursive caller keeps the same bindings after a rejected close and sees later compatible replacements.
    /// </summary>
    /// <param name="deferred">Whether the initial definitions are reconstructed before activation.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Definition_RetainsRecursiveAndExistingMethodBindings(bool deferred)
    {
        using var core = new ReplCore();
        var session = core.Session;
        session.DeferActivation = deferred;
        Submit(session,
            ".method int32 Step() { ldc.i4.1; ret }",
            ".method int32 Count(int32 n) {",
            "ldarg n", "brfalse BASE", "ldarg n", "ldc.i4.1", "sub", "call Count", "call Step", "add", "ret",
            "BASE: call Step", "ret", "}",
            ".method int32 Caller() { ldc.i4.3; call Count; ret }");
        session.Activate();
        var original = session.Methods.Single(method => method.Signature.Name == "Count");
        var caller = session.Methods.Single(method => method.Signature.Name == "Caller");
        var retained = original.Trampoline.Method.CreateDelegate<Func<int, int>>();
        session.AddLine("call Caller");
        Assert.AreEqual(4, session.Run().Value);
        Assert.AreEqual(4, retained(3));

        Submit(session, ".method int32 Count(int32 n) {", "call Step", "pop", "ldnull", "no. 1",
            "castclass object", "pop", "ldc.i4.s 99", "ret");
        var failure = Assert.ThrowsExactly<ReplException>(() => session.AddLine("}"));
        Assert.Contains("the JIT rejected method Count", failure.Message);
        Assert.AreSame(original, session.Methods.Single(method => method.Signature.Name == "Count"));
        Assert.AreEqual(4, retained(3));
        Assert.IsTrue(session.AbandonMethod());

        Submit(session,
            ".method int32 Count(int32 n) {",
            "ldarg n", "brfalse BASE", "ldarg n", "ldc.i4.1", "sub", "call Count",
            "call Step", "add", "call Step", "add", "ret", "BASE: call Step", "ret", "}");
        Assert.AreSame(original.Trampoline, session.Methods.Single(method => method.Signature.Name == "Count").Trampoline);
        Assert.AreEqual(7, retained(3));
        Submit(session, ".method int32 Step() { ldc.i4.2; ret }");
        Assert.AreSame(caller, session.Methods.Single(method => method.Signature.Name == "Caller"));
        session.AddLine("call Caller");
        Assert.AreEqual(14, session.Run().Value);
        Assert.AreEqual(14, retained(3));

        session.Reset();
        Assert.IsEmpty(session.Methods);
        Assert.AreEqual(14, retained(3));
    }

    private static void Submit(Session session, params string[] source)
    {
        foreach (var line in IlLines.Expand(source))
        {
            session.AddLine(line);
        }
    }
}
