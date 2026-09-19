using System.Collections;
using System.Reflection;
using System.Runtime.Loader;
using IlRepl.Engine;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Publishing an edit rebuilds dependent definitions atomically while previously captured originals remain pinned.
/// </summary>
[TestClass]
public sealed class MethodEditRebuildTests
{
    /// <summary>
    /// Direct callers, transitive callers, and the current cell use the new revision while captured originals remain unchanged.
    /// </summary>
    [TestMethod]
    public void Commit_RebuildsDirectAndTransitiveCallersAndPreservesCapturedBaselines()
    {
        var session = IlLines.Load(".method int32 Value() { ldc.i4.1; ret }");
        var edit = Commit(session, "Value");
        Add(session, ".method int32 Direct() { call int32 Copy(); ldc.i4.s 10; add; ret }",
            ".method int32 Transitive() { call int32 Direct(); ldc.i4 100; add; ret }",
            ".method int32 Unrelated() { ldc.i4 42; ret }");
        var direct = Method(session, "Direct");
        var transitive = Method(session, "Transitive");
        var unrelated = Method(session, "Unrelated");
        var frozenCaller = session.PrepareEdit("Direct", "FrozenCaller");
        var baseline = edit.OriginalMethod;
        var fingerprint = edit.Fingerprint;
        Assert.AreEqual(11, direct.Invoke(null, null));
        Assert.AreEqual(111, transitive.Invoke(null, null));
        session.AddLine("call int32 Transitive()");
        Assert.AreEqual(111, session.Run().Value);
        session.AddLine("call int32 Transitive()");

        session.CommitEdit(edit.Name, edit.Source.Replace("ldc.i4.1", "ldc.i4.2", StringComparison.Ordinal));

        Assert.AreEqual(12, Method(session, "Direct").Invoke(null, null));
        Assert.AreEqual(112, Method(session, "Transitive").Invoke(null, null));
        Assert.AreEqual(112, session.Run().Value);
        Assert.AreNotSame(direct, Method(session, "Direct"));
        Assert.AreNotSame(transitive, Method(session, "Transitive"));
        Assert.AreSame(unrelated, Method(session, "Unrelated"));
        Assert.AreEqual(42, unrelated.Invoke(null, null));
        Assert.AreSame(baseline, edit.OriginalMethod);
        Assert.AreEqual(1, baseline.Invoke(null, null));
        Assert.AreEqual(11, frozenCaller.OriginalMethod.Invoke(null, null));
        Assert.AreEqual(fingerprint, edit.Fingerprint);
        Assert.AreEqual(2, edit.Revision);
    }

    /// <summary>
    /// Dependent classes and constructed generic signatures refer to the newly published copied owner.
    /// </summary>
    [TestMethod]
    public void Commit_RebuildsDependentClassAndGenericTypeSignatures()
    {
        var session = IlLines.Load(".class public Counter {", ".field public int32 Value",
            ".method public instance void .ctor(int32 value) { ldarg.0; call instance void Object::.ctor(); "
            + "ldarg.0; ldarg.1; stfld int32 Counter::Value; ret }",
            ".method public instance int32 Next() { ldarg.0; ldfld int32 Counter::Value; ldc.i4.1; add; ret }", "}");
        var edit = Commit(session, "instance int32 Counter::Next()");
        var owner = edit.Method!.DeclaringType!;
        var name = owner.FullName!;
        Add(session, ".class public Box {", $".field public class {name} Item",
            ".method public instance void .ctor(int32 value) {", "ldarg.0", "call instance void Object::.ctor()",
            "ldarg.0", "ldarg.1", $"newobj instance void {name}::.ctor(int32)", $"stfld class {name} Box::Item", "ret", "}",
            ".method public instance int32 Read() {", "ldarg.0", $"ldfld class {name} Box::Item",
            $"callvirt instance int32 {name}::Next()", "ret", "}", "}",
            ".class public Holder<T> {", $".field public class {name} Item",
            $".method public static class {name} Identity(class {name} value) {{ ldarg.0; ret }}", "}",
            $".method class System.Collections.Generic.List`1<class {name}> MakeList() {{",
            $"newobj instance void class System.Collections.Generic.List`1<class {name}>::.ctor()", "ret", "}",
            ".method int32 ReadBox() { ldc.i4 40; newobj instance void Box::.ctor(int32); "
            + "callvirt instance int32 Box::Read(); ret }");
        var oldBox = Type(session, "Box");
        var oldHolder = Type(session, "Holder`1");
        var oldMakeList = Method(session, "MakeList");
        Assert.AreEqual(41, Method(session, "ReadBox").Invoke(null, null));
        Assert.AreSame(owner, oldMakeList.ReturnType.GetGenericArguments().Single());

        session.CommitEdit(edit.Name, edit.Source.Replace("ldc.i4.1", "ldc.i4.2", StringComparison.Ordinal));

        var currentOwner = edit.Method!.DeclaringType!;
        var box = Type(session, "Box");
        var holder = Type(session, "Holder`1");
        Assert.AreNotSame(owner, currentOwner);
        Assert.AreNotSame(oldBox, box);
        Assert.AreNotSame(oldHolder, holder);
        Assert.AreSame(currentOwner, box.GetField("Item")!.FieldType);
        Assert.AreSame(currentOwner, holder.GetField("Item")!.FieldType);
        Assert.AreSame(currentOwner, holder.GetMethod("Identity")!.ReturnType);
        Assert.AreSame(currentOwner, holder.GetMethod("Identity")!.GetParameters().Single().ParameterType);
        Assert.AreEqual(42, Method(session, "ReadBox").Invoke(null, null));
        var instance = Activator.CreateInstance(box, [40])!;
        Assert.AreEqual(42, box.GetMethod("Read")!.Invoke(instance, null));
        Assert.AreSame(currentOwner, box.GetField("Item")!.GetValue(instance)!.GetType());
        var listMethod = Method(session, "MakeList");
        Assert.AreNotSame(oldMakeList, listMethod);
        Assert.AreSame(currentOwner, listMethod.ReturnType.GetGenericArguments().Single());
        var list = (IList)listMethod.Invoke(null, null)!;
        Assert.IsEmpty(list);
        Assert.AreSame(currentOwner, list.GetType().GetGenericArguments().Single());
        var originalReceiver = Activator.CreateInstance(edit.OriginalMethod.DeclaringType!, [40]);
        Assert.AreEqual(41, edit.OriginalMethod.Invoke(originalReceiver, null));
        foreach (var image in new[]
        {
            AssemblyExporter.Write(session, "rebuilt-generic-signatures"),
            IlasmLocator.Assemble(IlAsmRenderer.Render(session)),
        })
        {
            var context = new AssemblyLoadContext("rebuilt-edit-" + Guid.NewGuid(), isCollectible: true);
            try
            {
                var exported = context.LoadFromStream(new MemoryStream(image));
                var methods = exported.GetTypes().SelectMany(type => type.GetMethods(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)).ToArray();
                Assert.AreEqual(42, methods.Single(method => method.Name == "ReadBox").Invoke(null, null));
                var exportedList = (IList)methods.Single(method => method.Name == "MakeList").Invoke(null, null)!;
                Assert.IsEmpty(exportedList);
                var exportedOwner = exportedList.GetType().GetGenericArguments().Single();
                Assert.AreSame(exported, exportedOwner.Assembly);
                Assert.AreSame(exportedOwner, exported.GetType("Box")!.GetField("Item")!.FieldType);
                Assert.AreSame(exportedOwner, exported.GetType("Holder`1")!.GetMethod("Identity")!.ReturnType);
            }
            finally
            {
                context.Unload();
            }
        }
    }

    /// <summary>
    /// Both validation errors and runtime preparation failures preserve every prior dependent binding and callable revision.
    /// </summary>
    /// <param name="runtimeFailure">Whether the candidate fails during actual runtime preparation.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Commit_FailurePreservesCallersAndPublishedRevision(bool runtimeFailure)
    {
        var session = IlLines.Load(".method int32 Value() { ldc.i4.1; ret }");
        var edit = Commit(session, "Value");
        Add(session, ".method int32 Direct() { call int32 Copy(); ret }",
            ".method int32 Transitive() { call int32 Direct(); ret }",
            ".class public Caller {", ".method public static int32 Read() { call int32 Copy(); ret }", "}");
        var source = edit.Source;
        var current = edit.Method;
        var caller = Type(session, "Caller");
        var direct = Method(session, "Direct");
        var transitive = Method(session, "Transitive");
        var generation = session.Generation;
        var replacement = runtimeFailure ? "ldnull\nno. 1\ncastclass object\npop\nldc.i4.3" : "ldstr \"wrong return type\"";

        var failure = Assert.ThrowsExactly<ReplException>(() => session.CommitEdit(edit.Name,
            source.Replace("ldc.i4.1", replacement, StringComparison.Ordinal)));

        Assert.Contains(runtimeFailure ? "runtime rejected" : "ret needs", failure.Message);
        Assert.AreSame(current, edit.Method);
        Assert.AreEqual(1, edit.Revision);
        Assert.AreEqual(source, edit.Source);
        Assert.AreEqual(generation, session.Generation);
        Assert.AreSame(caller, Type(session, "Caller"));
        Assert.AreSame(direct, Method(session, "Direct"));
        Assert.AreSame(transitive, Method(session, "Transitive"));
        Assert.AreEqual(1, current!.Invoke(null, null));
        Assert.AreEqual(1, direct.Invoke(null, null));
        Assert.AreEqual(1, transitive.Invoke(null, null));
        Assert.AreEqual(1, caller.GetMethod("Read")!.Invoke(null, null));
        session.CommitEdit(edit.Name, source.Replace("ldc.i4.1", "ldc.i4.2", StringComparison.Ordinal));
        Assert.AreEqual(2, Method(session, "Transitive").Invoke(null, null));
        Assert.AreEqual(2, Type(session, "Caller").GetMethod("Read")!.Invoke(null, null));
        Assert.AreEqual(1, edit.OriginalMethod.Invoke(null, null));
    }

    private static MethodEdit Commit(Session session, string reference)
    {
        var edit = session.PrepareEdit(reference, "Copy");
        return session.CommitEdit(edit.Name, edit.Source);
    }

    private static MethodInfo Method(Session session, string name) =>
        session.Methods.Single(method => method.Signature.Name == name).Version.Body;

    private static Type Type(Session session, string name) =>
        session.Types.Single(type => type.RuntimeType?.Name == name).RuntimeType!;

    private static void Add(Session session, params string[] lines)
    {
        foreach (var line in lines)
        {
            foreach (var split in IlLines.Expand(line))
            {
                session.AddLine(split);
            }
        }
    }
}
