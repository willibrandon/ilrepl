using System.Reflection;
using System.Runtime.Loader;
using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Tests.Shared;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Assembly activation preserves copied constructors, receiver scope, and framework validation.
/// </summary>
[TestClass]
public sealed class AssemblyActivationEditTests
{
    /// <summary>
    /// Supplies cancellation for real comparison processes.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Every overload constructs the copied type and preserves constructor state through comparisons and both export formats.
    /// </summary>
    /// <param name="overload">The number of activation parameters.</param>
    /// <param name="nested">Whether to activate a nested generic owner.</param>
    /// <param name="dispatch">The call, callvirt, or constrained tail dispatch.</param>
    [TestMethod]
    [DataRow(1, false, "callvirt")]
    [DataRow(2, false, "callvirt")]
    [DataRow(7, false, "callvirt")]
    [DataRow(1, true, "callvirt")]
    [DataRow(2, true, "callvirt")]
    [DataRow(7, true, "callvirt")]
    [DataRow(1, false, "call")]
    [DataRow(7, true, "call")]
    [DataRow(7, true, "tail")]
    public async Task Compare_AssemblyActivationPreservesConstructors(int overload, bool nested, string dispatch)
    {
        var session = IlLines.Load(AssemblyActivationExamples.Source(overload, nested, dispatch).Split('\n'));
        var edit = session.PrepareEdit("int32 Activation.Owner::Read(string)", "Copy");
        var name = ActivationExamples.Name(nested, overload != 1);
        Assert.AreEqual(42, edit.Original.Requested.Invoke(null, [name]));
        Assert.IsEmpty(edit.Problems, string.Join('\n', edit.Problems));
        session.CommitEdit(edit.Name, edit.Source);
        Assert.AreEqual(42, edit.Method!.Invoke(null, [name]));
        var arguments = "(" + LiteralParser.Escape(name) + ")";
        var same = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy " + arguments),
            TestContext.CancellationToken);
        Assert.AreEqual("match", same.Outcome, same.Original.Detail + "; " + same.Edited.Detail);
        Assert.AreEqual("42", same.Original.Result!.Value);
        Assert.AreEqual("42", same.Edited.Result!.Value);
        session.CommitEdit(edit.Name, AssemblyActivationExamples.Method(overload, nested, dispatch, true));
        var changed = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy " + arguments),
            TestContext.CancellationToken);
        Assert.AreEqual("different", changed.Outcome, changed.Original.Detail + "; " + changed.Edited.Detail);
        Assert.AreEqual("42", changed.Original.Result!.Value);
        Assert.AreEqual("43", changed.Edited.Result!.Value);
        session.AddLine("ldstr " + LiteralParser.Escape(name));
        session.AddLine("call Copy");
        foreach (var image in new[] { AssemblyExporter.Write(session, "assembly-activation"), IlasmLocator.Assemble(session.ToIlAsm()) })
        {
            var context = new AssemblyLoadContext("assembly-activation", isCollectible: true);
            try
            {
                var saved = context.LoadFromStream(new MemoryStream(image));
                Assert.AreEqual(43, saved.GetType("IlRepl.Cell")!.GetMethod("Run")!.Invoke(null, null));
                Assert.DoesNotContain(reference => reference.Name == "IlRepl.Engine", saved.GetReferencedAssemblies());
            }
            finally
            {
                context.Unload();
            }
        }
    }

    /// <summary>
    /// External receivers, null inputs, missing names, and constructor errors keep the runtime's original behavior.
    /// </summary>
    [TestMethod]
    public void Invoke_AssemblyActivationPreservesExternalReceiversAndErrors()
    {
        var session = IlLines.Load(".class public Activation.Owner {",
            ".method public instance void .ctor() { ldarg.0; call instance void Object::.ctor(); ret }",
            ".method public static object Make(class Assembly scope, string name, bool ignore, object[] args, object[] attributes) {",
            "ldarg.0", "ldarg.1", "ldarg.2", "ldc.i4 532", "ldnull", "ldarg.3", "ldnull", "ldarg.s 4",
            "callvirt instance object Assembly::CreateInstance(string, bool, valuetype BindingFlags, class Binder, "
                + "object[], class CultureInfo, object[])", "ret", "}", "}");
        var edit = session.PrepareEdit("object Activation.Owner::Make(class Assembly, string, bool, object[], object[])", "Copy");
        session.CommitEdit(edit.Name, edit.Source);
        var original = edit.Original.Requested;
        foreach (var method in new[] { original, edit.OriginalMethod, edit.Method! })
        {
            var owner = method.DeclaringType!;
            Assert.AreEqual(owner, method.Invoke(null, [owner.Assembly, "Activation.Owner", false, null, null])!.GetType());
            Assert.AreEqual(owner, method.Invoke(null, [owner.Assembly, "activation.owner", true, null, null])!.GetType());
            Assert.IsNull(method.Invoke(null, [owner.Assembly, "activation.owner", false, null, null]));
            Assert.IsNull(method.Invoke(null, [owner.Assembly, "Missing", false, null, null]));
            Assert.IsNull(method.Invoke(null, [owner.Assembly, owner.AssemblyQualifiedName, false, null, null]));
            Assert.AreEqual(original.DeclaringType,
                method.Invoke(null, [original.Module.Assembly, "Activation.Owner", false, null, null])!.GetType());
            Assert.AreEqual(0, method.Invoke(null, [typeof(int).Assembly, typeof(int).FullName, false, null, null]));
            foreach (var arguments in new object?[][]
            {
                [null, "Activation.Owner", false, null, null],
                [owner.Assembly, null, false, null, null],
                [owner.Assembly, "", false, null, null],
                [owner.Assembly, "Activation.Owner", false, new object[] { 42 }, null],
                [owner.Assembly, "Activation.Owner", false, null, new object[] { "unsupported" }],
            })
            {
                var sourceArguments = arguments.ToArray();
                if (sourceArguments[0] is Assembly)
                {
                    sourceArguments[0] = original.Module.Assembly;
                }

                var expected = Assert.ThrowsExactly<TargetInvocationException>(() => original.Invoke(null, sourceArguments));
                var actual = Assert.ThrowsExactly<TargetInvocationException>(() => method.Invoke(null, arguments));
                Assert.AreEqual(expected.InnerException!.GetType(), actual.InnerException!.GetType());
            }
        }
    }
}
