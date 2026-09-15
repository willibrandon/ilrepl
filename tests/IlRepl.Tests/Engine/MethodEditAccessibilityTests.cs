using System.Reflection;
using System.Runtime.Loader;
using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Protocol;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Edit aliases remain callable across nested private owners and visibility changes without altering original metadata.
/// </summary>
[TestClass]
public sealed class MethodEditAccessibilityTests
{
    /// <summary>
    /// Supplies cancellation for real comparison worker processes.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// A selected nested private owner retains its visibility while its scalar alias executes and exports independently.
    /// </summary>
    /// <param name="visibility">The selected method's own visibility inside its private owner.</param>
    /// <param name="depth">The number of private nested owners around the selected method.</param>
    [TestMethod]
    [DataRow("private", 1)]
    [DataRow("public", 1)]
    [DataRow("private", 3)]
    public async Task Alias_NestedPrivateOwnerRetainsMetadataAndSupportsExecution(string visibility, int depth)
    {
        var source = new List<string> { ".class public Outer {" };
        source.AddRange(Enumerable.Repeat(".class nested private Hidden {", depth));
        source.Add($".method {visibility} static int32 Read() {{ ldc.i4.s 41; ret }}");
        source.AddRange(Enumerable.Repeat("}", depth + 1));
        var session = IlLines.Load([.. source]);
        var path = "Outer" + string.Concat(Enumerable.Repeat("/Hidden", depth));
        var edit = session.PrepareEdit("int32 " + path + "::Read()", "ReadHidden");
        session.CommitEdit(edit.Name, edit.Source.Replace("ldc.i4.s 41", "ldc.i4.s 42", StringComparison.Ordinal));
        Assert.IsTrue(edit.Method!.DeclaringType!.IsNestedPrivate);
        Assert.AreEqual(visibility == "private", edit.Method.IsPrivate);
        Assert.IsTrue(edit.OriginalMethod.DeclaringType!.IsNestedPrivate);
        Assert.AreEqual(41, edit.OriginalMethod.Invoke(null, null));
        Add(session, ".method int32 Scenario() { call ReadHidden; ret }");
        session.AddLine("call ReadHidden");
        Assert.AreEqual(42, session.Run().Value);
        session.AddLine("call Scenario");

        AssertExports(session, edit, 42, visibility == "private", nestedPrivate: true);
        var compared = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "ReadHidden using Scenario"),
            TestContext.CancellationToken);
        AssertDifference(compared, "41", "42");
    }

    /// <summary>
    /// Nested owner arguments and method arguments remain distinct through public entries, exports, and observed generic calls.
    /// </summary>
    /// <param name="closeMethod">Whether the edit selects a closed method or the alias supplies its method argument.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Alias_NestedGenericOwnerAndMethodPreserveBindingsAndConstraints(bool closeMethod)
    {
        var session = IlLines.Load(".class public Outer`1<class T> {",
            ".class nested private Hidden`1<class T, valuetype .ctor U> {",
            ".method private static !!V Read<class V>(!0 owner, !1 number, !!V first, !!V second) {",
            "ldarg.0", "pop", "ldarg.1", "pop", "ldarg.2", "ret", "}", "}", "}");
        var argument = closeMethod ? "string" : "[1]";
        var edit = session.PrepareEdit("!!0 Outer`1/Hidden`1<string, int32>::Read<" + argument + ">(!0, !1, !!0, !!0)",
            "ReadHidden");
        session.CommitEdit(edit.Name, edit.Source.Replace("ldarg.2", "ldarg.3", StringComparison.Ordinal));
        var selected = (MethodInfo)edit.Method!;
        Assert.AreEqual(!closeMethod, selected.IsGenericMethodDefinition);
        Assert.IsTrue(selected.IsPrivate);
        Assert.IsTrue(selected.DeclaringType!.IsNestedPrivate);
        Assert.AreSequenceEqual(new[] { typeof(string), typeof(int) }, selected.DeclaringType.GenericTypeArguments);
        var method = selected.IsGenericMethodDefinition ? selected.MakeGenericMethod(typeof(string)) : selected;
        Assert.AreEqual("second", method.Invoke(null, ["owner", 42, "first", "second"]));
        var baseline = (MethodInfo)edit.OriginalMethod;
        if (baseline.IsGenericMethodDefinition)
        {
            baseline = baseline.MakeGenericMethod(typeof(string));
        }

        Assert.AreEqual("first", baseline.Invoke(null, ["owner", 42, "first", "second"]));
        var call = closeMethod ? "call ReadHidden" : "call ReadHidden<string>";
        Add(session, ".method string Scenario() {", "ldstr \"owner\"", "ldc.i4 42", "ldstr \"first\"", "ldstr \"second\"",
            call, "ret", "}");
        session.AddLine("call Scenario");
        Assert.AreEqual("second", session.Run().Value);
        session.AddLine("call Scenario");
        foreach (var image in new[] { AssemblyExporter.Write(session, "nested-generic-edit"),
            IlasmLocator.Assemble(session.ToIlAsm()) })
        {
            var context = new AssemblyLoadContext("nested-generic-edit-" + Guid.NewGuid(), isCollectible: true);
            try
            {
                var assembly = context.LoadFromStream(new MemoryStream(image));
                var owner = assembly.GetType(selected.DeclaringType.GetGenericTypeDefinition().FullName!)!;
                Assert.IsTrue(owner.IsNestedPrivate);
                Assert.ThrowsExactly<ArgumentException>(() => owner.MakeGenericType(typeof(int), typeof(int)));
                Assert.ThrowsExactly<ArgumentException>(() => owner.MakeGenericType(typeof(string), typeof(string)));
                var closed = owner.MakeGenericType(typeof(string), typeof(int));
                var exported = closed.GetMethod("Read", BindingFlags.Static | BindingFlags.NonPublic)!;
                Assert.IsTrue(exported.IsPrivate);
                Assert.ThrowsExactly<ArgumentException>(() => exported.MakeGenericMethod(typeof(int)));
                Assert.AreEqual("second", exported.MakeGenericMethod(typeof(string)).Invoke(null, ["owner", 42, "first", "second"]));
                Assert.AreEqual("second", assembly.GetType("IlRepl.Cell")!.GetMethod("Run")!.Invoke(null, null));
                Assert.AreEqual("second", assembly.GetType("IlRepl.Cell")!.GetMethod("Scenario")!.Invoke(null, null));
            }
            finally
            {
                context.Unload();
            }
        }

        var compared = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "ReadHidden using Scenario"),
            TestContext.CancellationToken);
        AssertDifference(compared, "first", "second");

        Add(session, ".method string Transitive() { call Scenario; ret }");
        var previousCaller = session.Methods.Single(method => method.Signature.Name == "Scenario").Version.Body;
        var previousTransitive = session.Methods.Single(method => method.Signature.Name == "Transitive").Version.Body;
        Assert.AreEqual("second", previousTransitive.Invoke(null, null));
        session.CommitEdit(edit.Name, edit.Source.Replace("ldarg.3", "ldarg.2", StringComparison.Ordinal));

        Assert.AreEqual("first", session.Run().Value);
        Assert.AreEqual("first", session.Methods.Single(method => method.Signature.Name == "Scenario").Version.Body.Invoke(null, null));
        Assert.AreEqual("first", session.Methods.Single(method => method.Signature.Name == "Transitive").Version.Body.Invoke(null, null));
        Assert.AreEqual("second", previousCaller.Invoke(null, null));
        Assert.AreEqual("second", previousTransitive.Invoke(null, null));
        Assert.AreEqual("first", baseline.Invoke(null, ["owner", 42, "first", "second"]));
        Assert.AreEqual(2, edit.Revision);
    }

    /// <summary>
    /// Changing method visibility rebuilds callers through the appropriate entry and preserves the captured public original.
    /// </summary>
    [TestMethod]
    public async Task Commit_PublicPrivatePublicRevisionsPreserveCallersAndBaseline()
    {
        var session = IlLines.Load(".class public Secret {", ".method public static int32 Read() { ldc.i4.1; ret }", "}");
        var edit = session.PrepareEdit("int32 Secret::Read()", "ReadSecret");
        session.CommitEdit(edit.Name, edit.Source);
        var baseline = edit.OriginalMethod;
        Add(session, ".method int32 Scenario() { call ReadSecret; ret }");
        var originalCaller = session.Methods.Single().Version.Body;
        Assert.AreEqual(1, originalCaller.Invoke(null, null));

        session.CommitEdit(edit.Name, edit.Source.Replace(".method public", ".method private", StringComparison.Ordinal)
            .Replace("ldc.i4.1", "ldc.i4.2", StringComparison.Ordinal));

        Assert.IsTrue(edit.Method!.IsPrivate);
        Assert.AreEqual(2, session.Methods.Single().Version.Body.Invoke(null, null));
        session.AddLine("call ReadSecret");
        AssertExports(session, edit, 2, isPrivate: true, nestedPrivate: false);

        session.CommitEdit(edit.Name, edit.Source.Replace(".method private", ".method public", StringComparison.Ordinal)
            .Replace("ldc.i4.2", "ldc.i4.3", StringComparison.Ordinal));

        Assert.IsTrue(edit.Method!.IsPublic);
        Assert.AreEqual(3, session.Run().Value);
        Assert.AreEqual(3, session.Methods.Single().Version.Body.Invoke(null, null));
        Assert.AreSame(baseline, edit.OriginalMethod);
        Assert.IsTrue(baseline.IsPublic);
        Assert.AreEqual(1, baseline.Invoke(null, null));
        Assert.AreEqual(1, originalCaller.Invoke(null, null));
        Assert.AreEqual(3, edit.Revision);
        session.AddLine("call ReadSecret");
        AssertExports(session, edit, 3, isPrivate: false, nestedPrivate: false);
        var compared = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "ReadSecret using Scenario"),
            TestContext.CancellationToken);
        AssertDifference(compared, "1", "3");
    }

    private static void AssertExports(Session session, MethodEdit edit, int expected, bool isPrivate, bool nestedPrivate)
    {
        foreach (var image in new[] { AssemblyExporter.Write(session, "edit-accessibility"),
            IlasmLocator.Assemble(session.ToIlAsm()) })
        {
            var context = new AssemblyLoadContext("edit-accessibility-" + Guid.NewGuid(), isCollectible: true);
            try
            {
                var assembly = context.LoadFromStream(new MemoryStream(image));
                var owner = assembly.GetType(edit.Method!.DeclaringType!.FullName!)!;
                var selected = owner.GetMethod("Read", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
                Assert.AreEqual(nestedPrivate, owner.IsNestedPrivate);
                Assert.AreEqual(isPrivate, selected.IsPrivate);
                Assert.AreEqual(expected, selected.Invoke(null, null));
                var cell = assembly.GetType("IlRepl.Cell")!;
                Assert.AreEqual(expected, cell.GetMethod("Run")!.Invoke(null, null));
                Assert.AreEqual(expected, cell.GetMethod("Scenario")!.Invoke(null, null));
                Assert.DoesNotContain(reference => reference.Name!.StartsWith("ilrepl_", StringComparison.Ordinal),
                    assembly.GetReferencedAssemblies());
            }
            finally
            {
                context.Unload();
            }
        }
    }

    private static void AssertDifference(ComparisonReply result, string original, string edited)
    {
        Assert.AreEqual("different", result.Outcome, result.Original.Detail + "; " + result.Edited.Detail);
        Assert.AreEqual("completed", result.Original.Outcome);
        Assert.AreEqual("completed", result.Edited.Outcome);
        Assert.AreEqual(original, result.Original.Result!.Value);
        Assert.AreEqual(edited, result.Edited.Result!.Value);
        Assert.HasCount(1, result.Original.Invocations);
        Assert.HasCount(1, result.Edited.Invocations);
    }

    private static void Add(Session session, params string[] source)
    {
        foreach (var line in IlLines.Expand(source))
        {
            session.AddLine(line);
        }
    }
}
