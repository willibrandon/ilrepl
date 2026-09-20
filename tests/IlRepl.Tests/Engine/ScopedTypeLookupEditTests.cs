using System.Reflection;
using System.Runtime.Loader;
using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Tests.Shared;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Assembly and module lookups preserve copied names through execution, comparisons, and standalone exports.
/// </summary>
[TestClass]
public sealed class ScopedTypeLookupEditTests
{
    /// <summary>
    /// Supplies cancellation for real comparison workers.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Nonvirtual calls retain the original base overload's dispatch through execution and export.
    /// </summary>
    /// <param name="scope">The Assembly or Module receiver type.</param>
    /// <param name="options">The number of Boolean lookup options.</param>
    [TestMethod]
    [DataRow("Assembly", 0)]
    [DataRow("Assembly", 1)]
    [DataRow("Module", 0)]
    [DataRow("Module", 1)]
    public async Task Compare_NonVirtualScopedLookupsKeepTheirDispatch(string scope, int options)
    {
        var source = ScopedTypeLookupExamples.Source(scope.ToLowerInvariant(), 0, options, false)
            .Replace("callvirt instance class Type " + scope + "::GetType", "call instance class Type " + scope + "::GetType",
                StringComparison.Ordinal);
        var session = IlLines.Load(source.Split('\n'));
        var edit = session.PrepareEdit("bool Lookup.Owner::Read()", "Copy");
        Assert.IsTrue((bool)edit.Original.Requested.Invoke(null, null)!);
        session.CommitEdit(edit.Name, edit.Source);
        Assert.IsTrue((bool)edit.Method!.Invoke(null, null)!);
        var comparison = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy ()"),
            TestContext.CancellationToken);
        Assert.AreEqual("match", comparison.Outcome, comparison.Original.Detail + "; " + comparison.Edited.Detail);
        Assert.AreEqual("true", comparison.Edited.Result!.Value);
        session.AddLine("call Copy");
        foreach (var image in new[] { AssemblyExporter.Write(session, "direct-lookups"), IlasmLocator.Assemble(session.ToIlAsm()) })
        {
            var context = new AssemblyLoadContext("direct-lookups", isCollectible: true);
            try
            {
                var assembly = context.LoadImage(image);
                Assert.IsTrue((bool)assembly.GetType("IlRepl.Cell")!.GetMethod("Run")!.Invoke(null, null)!);
            }
            finally
            {
                context.Unload();
            }
        }
    }

    /// <summary>
    /// Generic receiver references and tail calls remain valid after lookup adapters are emitted.
    /// </summary>
    /// <param name="scope">The Assembly or Module receiver type.</param>
    [TestMethod]
    [DataRow("Assembly")]
    [DataRow("Module")]
    public async Task Compare_ConstrainedGenericTailCallsRemainValid(string scope)
    {
        var session = IlLines.Load(ScopedTypeLookupExamples.TailSource(scope).Split('\n'));
        var edit = session.PrepareEdit("class Type Lookup.Owner::Read()", "Copy");
        Assert.AreEqual(edit.Original.Requested.DeclaringType, edit.Original.Requested.Invoke(null, null));
        Assert.IsEmpty(edit.Problems, string.Join('\n', edit.Problems));
        session.CommitEdit(edit.Name, edit.Source);
        Assert.AreEqual(edit.Method!.DeclaringType, edit.Method.Invoke(null, null));
        var comparison = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy ()"),
            TestContext.CancellationToken);
        Assert.AreEqual("match", comparison.Outcome, comparison.Original.Detail + "; " + comparison.Edited.Detail);
        Assert.HasCount(1, comparison.Original.Invocations);
        Assert.HasCount(1, comparison.Edited.Invocations);
        session.AddLine("call Copy");
        foreach (var image in new[] { AssemblyExporter.Write(session, "tail-lookups"), IlasmLocator.Assemble(session.ToIlAsm()) })
        {
            var context = new AssemblyLoadContext("tail-lookups", isCollectible: true);
            try
            {
                var assembly = context.LoadImage(image);
                Assert.AreEqual(assembly.GetType(edit.Method.DeclaringType!.FullName!),
                    assembly.GetType("IlRepl.Cell")!.GetMethod("Run")!.Invoke(null, null));
            }
            finally
            {
                context.Unload();
            }
        }
    }

    /// <summary>
    /// Instance lookups resolve owners, nested types, arrays, and constructed types with the requested options.
    /// </summary>
    /// <param name="scope">The receiver used by the lookup.</param>
    /// <param name="shape">The type-name shape to resolve.</param>
    /// <param name="options">The number of Boolean lookup options.</param>
    /// <param name="constrained">Whether the call uses a managed reference to the receiver.</param>
    [TestMethod]
    [DataRow("assembly", 0, 0, false)]
    [DataRow("assembly", 0, 1, false)]
    [DataRow("assembly", 0, 2, false)]
    [DataRow("executing", 0, 0, false)]
    [DataRow("module", 0, 0, false)]
    [DataRow("module", 0, 1, false)]
    [DataRow("module", 0, 2, false)]
    [DataRow("assembly", 1, 0, false)]
    [DataRow("module", 1, 2, false)]
    [DataRow("assembly", 2, 0, false)]
    [DataRow("module", 3, 0, false)]
    [DataRow("assembly", 4, 2, false)]
    [DataRow("module", 4, 0, false)]
    [DataRow("assembly", 0, 2, true)]
    [DataRow("module", 0, 2, true)]
    public async Task Compare_ScopedLookupsResolveCopiedTypes(string scope, int shape, int options, bool constrained)
    {
        var session = IlLines.Load(ScopedTypeLookupExamples.Source(scope, shape, options, constrained).Split('\n'));
        var edit = session.PrepareEdit("bool Lookup.Owner::Read()", "Copy");
        Assert.IsTrue((bool)edit.Original.Requested.Invoke(null, null)!);
        Assert.IsEmpty(edit.Problems, string.Join('\n', edit.Problems));
        session.CommitEdit(edit.Name, edit.Source);
        Assert.IsTrue((bool)edit.Method!.Invoke(null, null)!);
        var baseline = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy ()"),
            TestContext.CancellationToken);
        Assert.AreEqual("match", baseline.Outcome, baseline.Original.Detail + "; " + baseline.Edited.Detail);
        Assert.AreEqual("true", baseline.Original.Result!.Value);
        Assert.AreEqual("true", baseline.Edited.Result!.Value);
        session.CommitEdit(edit.Name, ScopedTypeLookupExamples.Method(scope, shape, options, constrained, true));
        var changed = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy ()"),
            TestContext.CancellationToken);
        Assert.AreEqual("different", changed.Outcome, changed.Original.Detail + "; " + changed.Edited.Detail);
        Assert.AreEqual("true", changed.Original.Result!.Value);
        Assert.AreEqual("false", changed.Edited.Result!.Value);
        session.AddLine("call Copy");
        foreach (var image in new[] { AssemblyExporter.Write(session, "scoped-lookups"), IlasmLocator.Assemble(session.ToIlAsm()) })
        {
            var context = new AssemblyLoadContext("scoped-lookups", isCollectible: true);
            try
            {
                var assembly = context.LoadImage(image);
                Assert.IsFalse((bool)assembly.GetType("IlRepl.Cell")!.GetMethod("Run")!.Invoke(null, null)!);
                Assert.DoesNotContain(reference => reference.Name == "IlRepl.Engine", assembly.GetReferencedAssemblies());
            }
            finally
            {
                context.Unload();
            }
        }
    }

    /// <summary>
    /// Lookup receivers from other assemblies retain their original names, null handling, and throwing behavior.
    /// </summary>
    /// <param name="scope">The assembly or module API.</param>
    [TestMethod]
    [DataRow("Assembly")]
    [DataRow("Module")]
    public void Invoke_ExternalReceiversAndInvalidInputsKeepTheirBehavior(string scope)
    {
        var session = IlLines.Load(".class public Lookup.Owner {",
            ".method public static class Type Read(class " + scope + " scope, string name, bool fail, bool ignore) {",
            "ldarg.0", "ldarg.1", "ldarg.2", "ldarg.3",
            "callvirt instance class Type " + scope + "::GetType(string, bool, bool)", "ret", "}", "}");
        var edit = session.PrepareEdit("class Type Lookup.Owner::Read(class " + scope + ", string, bool, bool)", "Copy");
        session.CommitEdit(edit.Name, edit.Source);
        foreach (var method in new[] { edit.Original.Requested, edit.OriginalMethod, edit.Method! })
        {
            var owner = method.DeclaringType!;
            var receiver = scope == "Assembly" ? (object)owner.Assembly : owner.Module;
            Assert.AreEqual(owner, method.Invoke(null, [receiver, "Lookup.Owner", true, false]));
            Assert.AreEqual(owner, method.Invoke(null, [receiver, "lookup.owner", true, true]));
            Assert.IsNull(method.Invoke(null, [receiver, "Lookup.Missing", false, false]));
            var missing = Assert.ThrowsExactly<TargetInvocationException>(() =>
                method.Invoke(null, [receiver, "Lookup.Missing", true, false]));
            Assert.IsInstanceOfType<TypeLoadException>(missing.InnerException);
            Assert.IsNull(method.Invoke(null, [receiver, owner.AssemblyQualifiedName, false, false]));
            var qualified = Assert.ThrowsExactly<TargetInvocationException>(() =>
                method.Invoke(null, [receiver, owner.AssemblyQualifiedName, true, false]));
            Assert.IsInstanceOfType<ArgumentException>(qualified.InnerException);
            var absent = Assert.ThrowsExactly<TargetInvocationException>(() => method.Invoke(null, [null, "Lookup.Owner", false, false]));
            Assert.IsInstanceOfType<NullReferenceException>(absent.InnerException);
            var nullName = Assert.ThrowsExactly<TargetInvocationException>(() => method.Invoke(null, [receiver, null, false, false]));
            Assert.IsInstanceOfType<ArgumentNullException>(nullName.InnerException);
            var sourceOwner = edit.Original.Requested.DeclaringType!;
            var source = scope == "Assembly" ? (object)sourceOwner.Assembly : sourceOwner.Module;
            Assert.AreEqual(sourceOwner, method.Invoke(null, [source, "Lookup.Owner", true, false]));
        }
    }
}
