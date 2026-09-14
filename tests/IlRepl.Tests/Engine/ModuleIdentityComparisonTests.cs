using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Tests.Shared;
using Mono.Cecil;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Fresh comparison processes observe the same generated module identity on both sides of a package.
/// </summary>
[TestClass]
public sealed class ModuleIdentityComparisonTests
{
    /// <summary>
    /// Supplies cancellation for real comparison processes.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// An unchanged body sees matching module IDs, while an explicit edited value remains distinguishable.
    /// </summary>
    /// <param name="generic">Whether the declaring owner is a constructed generic type.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Compare_ModuleIdentityIsSharedByBothImages(bool generic)
    {
        var session = IlLines.Load(ModuleIdentityExamples.Source(generic).Split('\n'));
        var edit = session.PrepareEdit(ModuleIdentityExamples.Reference(generic), "Copy");
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
        Assert.AreEqual(Convert.ToHexString(identity.ToByteArray()), same.Original.Result!.Value);
        Assert.AreEqual(same.Original.Result.Value, same.Edited.Result!.Value);
        session.CommitEdit(edit.Name, ModuleIdentityExamples.Method(generic, true));
        var changed = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy ()"),
            TestContext.CancellationToken);
        Assert.AreEqual("different", changed.Outcome, changed.Original.Detail + "; " + changed.Edited.Detail);
        Assert.AreEqual(Convert.ToHexString(Guid.Empty.ToByteArray()), changed.Edited.Result!.Value);
        Assert.AreNotEqual(changed.Original.Result!.Value, changed.Edited.Result.Value);
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
        using var module = ModuleDefinition.ReadModule(new MemoryStream(image));
        return module.Mvid;
    }
}
