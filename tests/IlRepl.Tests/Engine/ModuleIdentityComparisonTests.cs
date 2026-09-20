using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Tests.Shared;
using Mono.Cecil;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Source module identity requires recoverable rejection while comparison wrappers retain their own shared package identity.
/// </summary>
[TestClass]
public sealed class ModuleIdentityComparisonTests
{
    /// <summary>
    /// Supplies cancellation for real comparison processes.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Actual original module IDs remain observable while copied reads reject atomically and accept an explicit corrected value.
    /// </summary>
    /// <param name="generic">Whether the declaring owner is a constructed generic type.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Edit_SourceModuleIdentityRetainsRecoverableDraft(bool generic)
    {
        var session = IlLines.Load(ModuleIdentityExamples.Source(generic).Split('\n'));
        var edit = session.PrepareEdit(ModuleIdentityExamples.Reference(generic), "Copy");
        var original = edit.Original.Requested.Module.ModuleVersionId;
        Assert.AreNotEqual(Guid.Empty, original);
        Assert.AreEqual(original, edit.Original.Requested.Invoke(null, null));
        var problem = AssemblyLocationFixture.Problem("Module", "ModuleVersionId");
        Assert.Contains(item => item.Contains(problem, StringComparison.Ordinal), edit.Problems);
        var source = edit.Source;
        var completion = session.CompletionRevision;
        var error = Assert.ThrowsExactly<ReplException>(() => session.CommitEdit(edit.Name, source));
        Assert.Contains(problem, error.Message);
        Assert.AreEqual(source, edit.Source);
        Assert.AreEqual(0, edit.Revision);
        Assert.AreEqual(completion, session.CompletionRevision);
        Assert.IsNull(edit.Method);
        session.CommitEdit(edit.Name, ModuleIdentityExamples.Method(generic, true));
        Assert.IsEmpty(edit.Problems);
        Assert.AreEqual(Guid.Empty, edit.Method!.Invoke(null, null));
        var corrected = edit.Source;
        var previous = edit.Method;
        completion = session.CompletionRevision;
        error = Assert.ThrowsExactly<ReplException>(() => session.CommitEdit(edit.Name, source));
        Assert.Contains(problem, error.Message);
        Assert.AreSame(previous, edit.Method);
        Assert.AreEqual(corrected, edit.Source);
        Assert.AreEqual(1, edit.Revision);
        Assert.AreEqual(completion, session.CompletionRevision);
        Assert.AreEqual(original, edit.Original.Requested.Invoke(null, null));
        var result = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy ()"),
            TestContext.CancellationToken);
        Assert.AreEqual("different", result.Outcome, result.Original.Detail + "; " + result.Edited.Detail);
        Assert.AreEqual(Convert.ToHexString(original.ToByteArray()), result.Original.Result!.Value);
        Assert.AreEqual(Convert.ToHexString(Guid.Empty.ToByteArray()), result.Edited.Result!.Value);
        foreach (var side in new[] { result.Original, result.Edited })
        {
            Assert.AreEqual("completed", side.Outcome, side.Detail);
            Assert.IsNull(side.Exception);
            Assert.HasCount(1, side.Invocations);
        }
    }

    /// <summary>
    /// Shared generated MVIDs remain a package invariant independently of source code that inspects its own module identity.
    /// </summary>
    [TestMethod]
    public async Task Compare_WrapperModuleIdentityIsSharedByBothImages()
    {
        var session = IlLines.Load(".method public static valuetype Guid Read() { ldsfld valuetype Guid Guid::Empty; ret }");
        var edit = session.PrepareEdit("Read", "Copy");
        session.CommitEdit(edit.Name, edit.Source);
        var package = ComparisonCapture.Create(session, "Copy ()");
        var identity = Mvid(package.Original.Image);
        Assert.AreNotEqual(Guid.Empty, identity);
        Assert.AreEqual(identity, Mvid(package.Edited.Image));
        var next = ComparisonCapture.Create(session, "Copy ()");
        Assert.AreNotEqual(identity, Mvid(next.Original.Image));
        Assert.AreEqual(Mvid(next.Original.Image), Mvid(next.Edited.Image));
        var same = await ProcessComparisonRunner.RunAsync(package, TestContext.CancellationToken);
        Assert.AreEqual("match", same.Outcome, same.Original.Detail + "; " + same.Edited.Detail);
        Assert.AreEqual(Convert.ToHexString(Guid.Empty.ToByteArray()), same.Original.Result!.Value);
        Assert.AreEqual(same.Original.Result.Value, same.Edited.Result!.Value);
    }

    /// <summary>
    /// External-original wrappers share the generated MVID while retaining the real module ID used to verify the dependency.
    /// </summary>
    [TestMethod]
    public void Capture_ExternalOriginalKeepsItsModuleProvenance()
    {
        var session = new Session();
        var edit = session.PrepareEdit("int32 Math::Abs(int32)", "Copy");
        Assert.IsNotEmpty(edit.Problems);
        session.CommitEdit(edit.Name, ".method public static int32 Abs(int32 value) {\nldarg.0\nret\n}");
        var package = ComparisonCapture.Create(session, "Copy (1)");
        Assert.AreEqual(typeof(Math).Module.ModuleVersionId, package.Original.OriginalModule);
        Assert.AreEqual(Mvid(package.Original.Image), Mvid(package.Edited.Image));
        Assert.AreNotEqual(package.Original.OriginalModule, Mvid(package.Original.Image));
    }

    private static Guid Mvid(byte[] image)
    {
        using var moduleStream = new MemoryStream(image);
        using var module = ModuleDefinition.ReadModule(moduleStream);
        return module.Mvid;
    }
}
