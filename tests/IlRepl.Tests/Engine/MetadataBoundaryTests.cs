using System.Reflection;
using System.Runtime.Loader;
using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Protocol;
using IlRepl.Tests.Shared;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Actual external inspectors preserve original metadata identity or reject copied context before an edit is published.
/// </summary>
[TestClass]
public sealed class MetadataBoundaryTests
{
    /// <summary>
    /// Supplies cancellation for real isolated comparison processes.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Enumerates distinct metadata families and provenance routes crossing the external boundary.
    /// </summary>
    public static IEnumerable<(string Target, string Flow, string Origin)> RejectedCases => MetadataBoundaryFixture.RejectedCases;

    /// <summary>
    /// Enumerates unchanged external and sibling metadata, null, ordinary objects and whole ordinary arrays.
    /// </summary>
    public static IEnumerable<(string Target, string Flow, string Origin)> SupportedCases => MetadataBoundaryFixture.SupportedCases;

    /// <summary>
    /// Copied metadata crossing a true external boundary rejects atomically and retains a correct executable original during recovery.
    /// </summary>
    /// <param name="target">The metadata value family.</param>
    /// <param name="flow">The typed, erased, stored, copied-helper, boxed, or complete-array route.</param>
    /// <param name="origin">The original producer of the metadata value.</param>
    [TestMethod]
    [DynamicData(nameof(RejectedCases))]
    public async Task Edit_ExternalMetadataIdentityBoundaryRejectsAndRecovers(string target, string flow, string origin)
    {
        var images = MetadataBoundaryFixture.Create(target, flow, origin);
        var session = new Session();
        var source = session.Resolver.LoadImage(images.Source);
        var inspector = session.Resolver.LoadImage(images.Inspector);
        AssertSource(source, inspector);
        var original = source.GetType("MetadataBoundary.Owner")!.GetMethod("Read")!;
        Assert.AreEqual(42, original.Invoke(null, null));
        var edit = session.PrepareEdit("int32 [" + images.SourceName + "]MetadataBoundary.Owner::Read()", "Copy");
        AssertProblem(edit, flow);
        AssertCallbackCount(source, flow, 1);
        var initial = edit.Source;
        var completion = session.CompletionRevision;
        var failure = Assert.ThrowsExactly<ReplException>(() => session.CommitEdit(edit.Name, initial));
        Assert.Contains("external", failure.Message);
        Assert.Contains("copied metadata identity", failure.Message);
        Assert.IsNull(edit.Method);
        Assert.AreEqual(0, edit.Revision);
        Assert.AreEqual(initial, edit.Source);
        Assert.AreEqual(completion, session.CompletionRevision);
        AssertCallbackCount(source, flow, 1);
        session.CommitEdit(edit.Name, ".method public static int32 Read() {\nldc.i4.s 43\nret\n}");
        Assert.IsEmpty(edit.Problems);
        var corrected = edit.Source;
        var previous = edit.Method;
        Assert.AreEqual(43, previous!.Invoke(null, null));
        completion = session.CompletionRevision;
        failure = Assert.ThrowsExactly<ReplException>(() => session.CommitEdit(edit.Name, initial));
        Assert.Contains("copied metadata identity", failure.Message);
        Assert.AreSame(previous, edit.Method);
        Assert.AreEqual(corrected, edit.Source);
        Assert.AreEqual(1, edit.Revision);
        Assert.AreEqual(completion, session.CompletionRevision);
        Assert.AreEqual(43, edit.Method!.Invoke(null, null));
        AssertCallbackCount(source, flow, 1);
        Assert.AreEqual(42, original.Invoke(null, null));
        AssertCallbackCount(source, flow, 2);
        await AssertComparison(session, 43);
        AssertExports(session, source, inspector, 43, retained: false);
    }

    /// <summary>
    /// Known unchanged metadata and ordinary values retain actual external helper identity through revisions, workers and exports.
    /// </summary>
    /// <param name="target">The metadata or ordinary value family.</param>
    /// <param name="flow">The typed, local, boxed, or complete-array argument route.</param>
    /// <param name="origin">The BCL, public sibling, ordinary object, or null producer.</param>
    [TestMethod]
    [DynamicData(nameof(SupportedCases))]
    public async Task Edit_UnchangedExternalMetadataAndOrdinaryValuesRemainSupported(string target, string flow, string origin)
    {
        var images = MetadataBoundaryFixture.Create(target, flow, origin);
        var session = new Session();
        var source = session.Resolver.LoadImage(images.Source);
        var inspector = session.Resolver.LoadImage(images.Inspector);
        AssertSource(source, inspector);
        var original = source.GetType("MetadataBoundary.Owner")!.GetMethod("Read")!;
        Assert.AreEqual(42, original.Invoke(null, null));
        var edit = session.PrepareEdit("int32 [" + images.SourceName + "]MetadataBoundary.Owner::Read()", "Copy");
        Assert.IsEmpty(edit.Problems, string.Join("; ", edit.Problems));
        AssertCallbackCount(source, flow, 1);
        session.CommitEdit(edit.Name, edit.Source);
        AssertCallbackCount(source, flow, 1);
        Assert.AreEqual(42, edit.Method!.Invoke(null, null));
        AssertCallbackCount(source, flow, 1);
        if (flow.StartsWith("callback-", StringComparison.Ordinal))
        {
            Assert.AreEqual(1, edit.Method.DeclaringType!.GetField("Calls")!.GetValue(null));
        }

        var external = MethodDisassembler.Disassemble(edit.Method, session).Entries
            .Select(entry => entry.Instruction?.Operand).OfType<ResolvedMethod>()
            .Select(method => method.Method!).Single(method => method.Name == MetadataBoundaryFixture.CalledMethod(flow));
        Assert.AreSame(inspector, external.Module.Assembly);
        Assert.Contains(dependency => dependency.Symbol.Contains("Inspector::" + MetadataBoundaryFixture.CalledMethod(flow),
            StringComparison.Ordinal)
            && dependency.Disposition == "external" && dependency.Access == "public", edit.Dependencies);
        await AssertComparison(session, 42);
        AssertExports(session, source, inspector, 42, retained: true);
        var position = edit.Source.LastIndexOf("ret", StringComparison.Ordinal);
        Assert.IsGreaterThan(0, position);
        session.CommitEdit(edit.Name, edit.Source.Insert(position, "ldc.i4.1\nadd\n"));
        Assert.AreEqual(43, edit.Method!.Invoke(null, null));
        AssertCallbackCount(source, flow, 1);
        Assert.AreEqual(42, original.Invoke(null, null));
        AssertCallbackCount(source, flow, 2);
        Assert.AreEqual(42, edit.OriginalMethod.Invoke(null, null));
        await AssertComparison(session, 43);
        AssertExports(session, source, inspector, 43, retained: true);
    }

    private static void AssertSource(Assembly source, Assembly inspector)
    {
        Assert.AreNotSame(source, inspector);
        Assert.Contains(reference => reference.Name == inspector.GetName().Name, source.GetReferencedAssemblies());
        Assert.Contains(reference => reference.Name == source.GetName().Name, inspector.GetReferencedAssemblies());
        var owner = source.GetType("MetadataBoundary.Owner")!;
        var sibling = source.GetType("MetadataBoundary.Sibling")!;
        Assert.AreNotSame(owner, sibling);
        Assert.IsTrue(sibling.IsPublic);
        Assert.AreSame(owner.Assembly, sibling.Assembly);
        Assert.AreSame(owner.Module, sibling.Module);
    }

    private static void AssertCallbackCount(Assembly source, string flow, int expected)
    {
        if (flow.StartsWith("callback-", StringComparison.Ordinal))
        {
            Assert.AreEqual(expected, source.GetType("MetadataBoundary.Owner")!.GetField("Calls")!.GetValue(null));
        }
    }

    private static void AssertProblem(MethodEdit edit, string flow)
    {
        Assert.Contains(problem => problem.Contains("external", StringComparison.Ordinal)
            && problem.Contains("copied metadata identity", StringComparison.Ordinal), edit.Problems, string.Join("; ", edit.Problems));
        Assert.Contains(dependency => dependency.Symbol.Contains(MetadataBoundaryFixture.Sink(flow), StringComparison.Ordinal)
            && dependency.Disposition.Contains("copied metadata identity", StringComparison.Ordinal), edit.Dependencies);
    }

    private async Task AssertComparison(Session session, int expected)
    {
        var result = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy ()"), TestContext.CancellationToken);
        Assert.AreEqual(expected == 42 ? "match" : "different", result.Outcome, result.Original.Detail + "; " + result.Edited.Detail);
        AssertSide(result.Original, "42");
        AssertSide(result.Edited, expected.ToString());
    }

    private static void AssertSide(ComparisonSide side, string expected)
    {
        Assert.AreEqual("completed", side.Outcome, side.Detail);
        Assert.IsNull(side.Exception);
        Assert.IsNotNull(side.Result);
        Assert.AreEqual("scalar", side.Result.Kind);
        Assert.AreEqual(expected, side.Result.Value);
        var invocation = Assert.ContainsSingle(side.Invocations);
        Assert.IsNull(invocation.Exception);
        Assert.AreEqual("null", invocation.Inputs.Single(member => member.Name == "receiver").Value.Kind);
        var returned = invocation.Outputs.Single(member => member.Name == "return").Value;
        Assert.AreEqual("scalar", returned.Kind);
        Assert.AreEqual(expected, returned.Value);
    }

    private static void AssertExports(Session session, Assembly source, Assembly inspector, int expected, bool retained)
    {
        session.AddLine("call Copy");
        var images = new[] { AssemblyExporter.Write(session, "metadata-boundary-copy"), IlasmLocator.Assemble(session.ToIlAsm()) };
        session.ClearCell();
        foreach (var image in images)
        {
            var context = new AssemblyLoadContext("metadata-boundary-copy", isCollectible: true);
            if (retained)
            {
                context.Resolving += (_, name) => name.Name == source.GetName().Name ? source
                : name.Name == inspector.GetName().Name ? inspector : null;
            }

            try
            {
                var exported = context.LoadImage(image);
                if (retained)
                {
                    Assert.Contains(reference => reference.Name == inspector.GetName().Name, exported.GetReferencedAssemblies());
                }
                else
                {
                    Assert.DoesNotContain(reference => reference.Name == inspector.GetName().Name, exported.GetReferencedAssemblies());
                }

                Assert.AreEqual(expected, exported.GetType("IlRepl.Cell")!.GetMethod("Run")!.Invoke(null, null));
            }
            finally
            {
                context.Unload();
            }
        }
    }
}
