using System.Reflection;
using System.Runtime.Loader;
using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Tests.Shared;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Reflected constructors preserve executable context and downstream instance provenance without running during preflight.
/// </summary>
[TestClass]
public sealed class ConstructorReflectionTests
{
    /// <summary>
    /// Supplies cancellation for real comparison workers.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Actual constructors, downstream reflective methods, comparison processes and both export formats return 42 and edited 43.
    /// </summary>
    /// <param name="lookup">The actual constructor lookup API.</param>
    /// <param name="argument">Whether the constructor receives one integer.</param>
    /// <param name="privateConstructor">Whether the selected constructor is private.</param>
    /// <param name="flow">The receiver provenance.</param>
    /// <param name="invoker">Whether to allocate through ConstructorInvoker.</param>
    /// <param name="generic">Whether the declaring owner is generic.</param>
    /// <returns>The completed preflight, runtime, process and export assertions.</returns>
    [TestMethod]
    [DataRow("one", false, false, "direct", false, false)]
    [DataRow("one", true, false, "local", false, false)]
    [DataRow("two", false, true, "local", false, false)]
    [DataRow("two", true, true, "return", false, false)]
    [DataRow("four", false, false, "return", false, false)]
    [DataRow("four", true, true, "direct", false, false)]
    [DataRow("five", false, true, "direct", false, false)]
    [DataRow("five", true, false, "local", false, false)]
    [DataRow("index", false, false, "direct", false, false)]
    [DataRow("index", true, true, "local", false, false)]
    [DataRow("first", true, false, "return", false, false)]
    [DataRow("single", false, true, "return", false, false)]
    [DataRow("declared", true, true, "return", false, false)]
    [DataRow("as-type", true, false, "direct", false, false)]
    [DataRow("one", false, false, "direct", true, false)]
    [DataRow("four", true, true, "local", true, true)]
    [DataRow("two", true, true, "return", false, true)]
    public async Task Compare_ReflectedConstructorsPreserveContext(string lookup, bool argument, bool privateConstructor,
        string flow, bool invoker, bool generic)
    {
        var source = ConstructorReflectionExamples.Source(lookup, argument, privateConstructor, flow, invoker, generic);
        var session = IlLines.Load(source.Split('\n'));
        var edit = session.PrepareEdit(ConstructorReflectionExamples.Reference(generic), "Copy");
        Assert.IsEmpty(edit.Problems, string.Join("; ", edit.Problems));
        Assert.AreEqual(0, Counter(edit.Original.Requested));
        Assert.AreEqual(0, Counter(edit.OriginalMethod));
        session.CommitEdit(edit.Name, edit.Source);
        Assert.AreEqual(0, Counter(edit.Method!));
        Assert.AreEqual(0, Counter(edit.Original.Requested));
        Assert.AreEqual(0, Counter(edit.OriginalMethod));
        foreach (var method in new[] { edit.Original.Requested, edit.OriginalMethod, edit.Method! })
        {
            Assert.AreEqual(42, method.Invoke(null, null));
            Assert.AreEqual(1, Counter(method));
        }
        await AssertComparisonAsync(session, "match", "42");
        session.CommitEdit(edit.Name, ConstructorReflectionExamples.Method(lookup, argument, privateConstructor,
            flow, invoker, generic, edited: true));
        Assert.AreEqual(0, Counter(edit.Method!));
        Assert.AreEqual(1, Counter(edit.Original.Requested));
        Assert.AreEqual(1, Counter(edit.OriginalMethod));
        Assert.AreEqual(43, edit.Method!.Invoke(null, null));
        Assert.AreEqual(1, Counter(edit.Method));
        await AssertComparisonAsync(session, "different", "43");
        AssertExports(session);
    }

    /// <summary>
    /// TypeInitializer metadata invokes the actual static constructor through the nonallocating MethodBase API.
    /// </summary>
    /// <returns>The completed original, copy, worker and export assertions.</returns>
    [TestMethod]
    public async Task Compare_TypeInitializerRetainsItsActualStaticConstructor()
    {
        var session = IlLines.Load(ConstructorReflectionExamples.InitializerSource().Split('\n'));
        var edit = session.PrepareEdit(ConstructorReflectionExamples.Reference(), "Copy");
        Assert.AreEqual(42, edit.Original.Requested.Invoke(null, null));
        Assert.IsEmpty(edit.Problems, string.Join("; ", edit.Problems));
        session.CommitEdit(edit.Name, edit.Source);
        Assert.AreEqual(42, edit.Method!.Invoke(null, null));
        await AssertComparisonAsync(session, "match", "42");
        var source = edit.Source;
        session.CommitEdit(edit.Name, source.Insert(source.LastIndexOf("ret", StringComparison.Ordinal), "ldc.i4.1\nadd\n"));
        Assert.AreEqual(43, edit.Method!.Invoke(null, null));
        await AssertComparisonAsync(session, "different", "43");
        AssertExports(session);
    }

    /// <summary>
    /// Unknown constructor or custom-binder inputs execute genuinely in the original and reject unsupported drafts atomically.
    /// </summary>
    /// <param name="flow">An unknown constructor receiver or an unknown custom binder.</param>
    /// <param name="invoker">Whether ConstructorInvoker consumes the constructor.</param>
    [TestMethod]
    [DataRow("unknown", false)]
    [DataRow("unknown", true)]
    [DataRow("binder", false)]
    [DataRow("binder", true)]
    public void Edit_UnknownConstructorTargetsRemainRecoverable(string flow, bool invoker)
    {
        var session = IlLines.Load(ConstructorReflectionExamples.Source("four", true, false, flow, invoker).Split('\n'));
        var edit = session.PrepareEdit(ConstructorReflectionExamples.Reference(flow: flow), "Copy");
        var owner = edit.Original.Requested.DeclaringType!;
        var binder = new ConstructorReflectionBinder();
        object input = flow == "unknown" ? owner.GetConstructor([typeof(int)])! : binder;
        Assert.AreEqual(0, Counter(edit.Original.Requested));
        Assert.AreEqual(0, binder.Calls);
        Assert.Contains(problem => problem.Contains("indirect reflection cannot prove a supported target", StringComparison.Ordinal),
            edit.Problems);
        var source = edit.Source;
        var revision = session.CompletionRevision;
        var failure = Assert.ThrowsExactly<ReplException>(() => session.CommitEdit(edit.Name, source));
        Assert.Contains("indirect reflection cannot prove a supported target", failure.Message);
        Assert.AreEqual(source, edit.Source);
        Assert.IsNull(edit.Method);
        Assert.AreEqual(0, edit.Revision);
        Assert.AreEqual(revision, session.CompletionRevision);
        Assert.AreEqual(0, Counter(edit.Original.Requested));
        Assert.AreEqual(0, binder.Calls);
        Assert.AreEqual(42, edit.Original.Requested.Invoke(null, [input]));
        Assert.AreEqual(1, Counter(edit.Original.Requested));
        if (flow == "binder") Assert.IsGreaterThan(0, binder.Calls);
        var parameter = flow == "unknown" ? "class ConstructorInfo constructor" : "class Binder binder";
        var corrected = ".method public static int32 Read(" + parameter + ") {\nldc.i4.s 42\nret\n}";
        session.CommitEdit(edit.Name, corrected);
        Assert.AreEqual(42, edit.Method!.Invoke(null, [input]));
        var previous = edit.Method;
        revision = session.CompletionRevision;
        Assert.ThrowsExactly<ReplException>(() => session.CommitEdit(edit.Name, source));
        Assert.AreSame(previous, edit.Method);
        Assert.AreEqual(corrected, edit.Source);
        Assert.AreEqual(1, edit.Revision);
        Assert.AreEqual(revision, session.CompletionRevision);
    }

    private static object? Counter(MethodBase method) => method.DeclaringType!.GetField("Constructions")!.GetValue(null);

    private async Task AssertComparisonAsync(Session session, string outcome, string edited)
    {
        var result = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy ()"), TestContext.CancellationToken);
        Assert.AreEqual(outcome, result.Outcome, result.Original.Detail + "; " + result.Edited.Detail);
        foreach (var (side, expected) in new[] { (result.Original, "42"), (result.Edited, edited) })
        {
            Assert.AreEqual("completed", side.Outcome, side.Detail);
            Assert.IsNull(side.Exception, side.Detail);
            Assert.AreEqual(expected, side.Result!.Value);
            Assert.HasCount(1, side.Invocations);
        }
    }

    private static void AssertExports(Session session)
    {
        session.AddLine("call Copy");
        foreach (var image in new[] { AssemblyExporter.Write(session, "constructor-copy"), IlasmLocator.Assemble(session.ToIlAsm()) })
        {
            var context = new AssemblyLoadContext("constructor-copy", isCollectible: true);
            try
            {
                var assembly = context.LoadFromStream(new MemoryStream(image));
                Assert.AreEqual(43, assembly.GetType("IlRepl.Cell")!.GetMethod("Run")!.Invoke(null, null));
            }
            finally
            {
                context.Unload();
            }
        }
    }
}
