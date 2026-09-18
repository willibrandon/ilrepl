using IlRepl.Engine;
using IlRepl.Repl;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Retained binding contexts keep their signatures while later definitions and resets update new cells.
/// </summary>
[TestClass]
public sealed class SessionSignatureTests
{
    /// <summary>
    /// Signature replacement changes subsequent binding without mutating an earlier context or surviving reset.
    /// </summary>
    [TestMethod]
    public void RetainedContext_PreservesSignatureAcrossReplacementAndReset()
    {
        using var core = new ReplCore();
        var session = core.Session;
        session.AddLine(".method int32 Answer() {");
        session.AddLine("ldc.i4.s 42");
        session.AddLine("ret");
        session.AddLine("}");
        var earlier = session.InspectionContext;
        session.AddLine("call int32 Answer()");
        Assert.AreEqual(42, session.Run().Value);
        session.AddLine(".method string Answer() {");
        session.AddLine("ldstr \"updated\"");
        session.AddLine("ret");
        session.AddLine("}");
        var updated = session.InspectionContext;
        Assert.AreEqual(typeof(int), earlier.Methods.Single().ReturnType);
        Assert.AreEqual(typeof(string), updated.Methods.Single().ReturnType);
        session.AddLine("call string Answer()");
        Assert.AreEqual("updated", session.Run().Value);
        session.Reset();
        Assert.IsEmpty(session.InspectionContext.Methods);
        Assert.AreEqual(typeof(int), earlier.Methods.Single().ReturnType);
        Assert.AreEqual(typeof(string), updated.Methods.Single().ReturnType);
        Assert.ThrowsExactly<ReplException>(() => session.AddLine("call string Answer()"));
    }
}
