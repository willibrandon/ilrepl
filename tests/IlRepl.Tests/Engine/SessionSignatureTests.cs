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
    /// New and replacement method bodies bind an ordered private snapshot without changing already retained contexts.
    /// </summary>
    /// <param name="count">The number of methods accepted before opening the next body.</param>
    /// <param name="replacement">The replaced position, or minus one to append a new method.</param>
    [TestMethod]
    [DataRow(0, -1)]
    [DataRow(1, -1)]
    [DataRow(3, -1)]
    [DataRow(3, 0)]
    [DataRow(3, 1)]
    [DataRow(3, 2)]
    public void OpenMethod_PreservesOrderedBindingSnapshots(int count, int replacement)
    {
        using var core = new ReplCore();
        var session = core.Session;
        for (var index = 0; index < count; index++)
        {
            session.AddLine($".method int32 Value{index}() {{");
            session.AddLine($"ldc.i4 {index}");
            session.AddLine("ret");
            session.AddLine("}");
        }
        var before = session.InspectionContext;
        var previous = before.Methods.ToArray();
        var name = replacement < 0 ? "Added" : $"Value{replacement}";
        var slot = replacement < 0 ? count : replacement;
        session.AddLine($".method int32 {name}() {{");
        var opened = session.State.Context;
        Assert.HasCount(count + (replacement < 0 ? 1 : 0), opened.Methods);
        Assert.AreEqual(name, opened.Methods[slot].Name);
        for (var index = 0; index < count; index++)
        {
            Assert.AreSame(previous[index], before.Methods[index]);
            if (index == replacement) Assert.AreNotSame(previous[index], opened.Methods[index]);
            else Assert.AreSame(previous[index], opened.Methods[index]);
        }
        session.AddLine("ldc.i4.0");
        session.AddLine("brfalse done");
        session.AddLine($"call int32 {name}()");
        session.AddLine("ret");
        session.AddLine("done: ldc.i4.s 42");
        session.AddLine("ret");
        session.AddLine("}");
        Assert.AreSame(opened.Methods[slot], session.InspectionContext.Methods[slot]);
        session.AddLine($"call int32 {name}()");
        Assert.AreEqual(42, session.Run().Value);
        for (var index = 0; index < count; index++)
        {
            session.AddLine($"call int32 Value{index}()");
            Assert.AreEqual(index == replacement ? 42 : index, session.Run().Value);
        }
        session.Reset();
        Assert.IsEmpty(session.InspectionContext.Methods);
        Assert.AreEqual(name, opened.Methods[slot].Name);
        Assert.AreSequenceEqual(previous, before.Methods);
    }

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
