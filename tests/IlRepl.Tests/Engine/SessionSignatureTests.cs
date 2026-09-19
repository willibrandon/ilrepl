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
    /// Type reconstruction publishes runtime signatures or restores the original bindings after rejecting a dependent body.
    /// </summary>
    /// <param name="accepted">Whether the replacement preserves the field required by its dependent method.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RebuiltDefinitions_RefreshSignaturesOrRestoreOriginalBindings(bool accepted)
    {
        using var core = new ReplCore();
        var session = core.Session;
        AddType("Value", 7);
        Add(".method class Item Make() { newobj instance void Item::.ctor(); ret }");
        Add(".method int32 Read(class Item item) { ldarg item; ldfld int32 Item::Value; ret }");
        var before = session.InspectionContext;
        var original = before.Methods.ToArray();
        var oldType = original.Single(method => method.Name == "Make").ReturnType;

        if (accepted)
        {
            AddType("Value", 42);
        }
        else
        {
            var error = Assert.ThrowsExactly<ReplException>(() => AddType("Other", 42));
            Assert.Contains("cannot redefine class Item: method Read:", error.Message);
            Assert.Contains("Value", error.Message);
        }

        var current = session.Types.Single().RuntimeType;
        var after = session.InspectionContext;
        Assert.AreSame(current, after.Methods.Single(method => method.Name == "Make").ReturnType);
        Assert.AreSame(current, after.Methods.Single(method => method.Name == "Read").Parameters.Single().Type);
        Assert.AreSame(oldType, before.Methods.Single(method => method.Name == "Make").ReturnType);
        Assert.AreSequenceEqual(original, before.Methods);
        if (accepted)
        {
            Assert.AreNotSame(oldType, current);
        }
        else
        {
            Assert.AreSame(oldType, current);
            for (var index = 0; index < original.Length; index++)
            {
                Assert.AreSame(original[index], after.Methods[index]);
            }
        }

        for (var attempt = 0; attempt < 2; attempt++)
        {
            Add("call class Item Make()", "call int32 Read(class Item)");
            Assert.AreEqual(accepted ? 42 : 7, session.Run().Value);
        }

        void AddType(string field, int value) => Add(".class public Item {", $".field public int32 {field}",
            ".method public instance void .ctor() {", "ldarg.0", "call instance void [System.Runtime]System.Object::.ctor()",
            "ldarg.0", $"ldc.i4 {value}", $"stfld int32 Item::{field}", "ret", "}", "}");

        void Add(params string[] source)
        {
            foreach (var line in IlLines.Expand(source))
            {
                session.AddLine(line);
            }
        }
    }

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
            if (index == replacement)
            {
                Assert.AreNotSame(previous[index], opened.Methods[index]);
            }
            else
            {
                Assert.AreSame(previous[index], opened.Methods[index]);
            }
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
