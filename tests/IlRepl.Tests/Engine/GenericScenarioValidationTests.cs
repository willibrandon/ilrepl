using IlRepl.Engine;
using IlRepl.Host;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Comparison scenarios require a closed entry point while their bodies can supply generic arguments to edited methods.
/// </summary>
[TestClass]
public sealed class GenericScenarioValidationTests
{
    /// <summary>
    /// Supplies cancellation for real comparison workers.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Generic scenario declarations are rejected and a parameterless wrapper can compare a closed generic call.
    /// </summary>
    [TestMethod]
    public async Task Capture_RejectsGenericScenarioDeclarationAndComparesWrapper()
    {
        var session = IlLines.Load(".class public Owner {",
            ".method public static int32 Read<T>() { ldc.i4.1; ret }", "}");
        var edit = session.PrepareEdit("int32 Owner::Read<[1]>()", "Copy");
        session.CommitEdit(edit.Name, edit.Source.Replace("ldc.i4.1", "ldc.i4.2", StringComparison.Ordinal));
        var declaration = Assert.ThrowsExactly<ReplException>(() => session.AddLine(".method int32 Open<T>() {"));
        Assert.Contains("bad method name 'Open<T>'", declaration.Message);
        Assert.IsEmpty(session.Methods);
        Assert.IsNull(session.OpenMethod);
        var capture = Assert.ThrowsExactly<ReplException>(() => ComparisonCapture.Create(session, "Copy using Open"));
        Assert.Contains("no session scenario 'Open'", capture.Message);
        foreach (var line in IlLines.Expand(".method int32 Scenario() { call Copy<int32>; ret }"))
        {
            session.AddLine(line);
        }

        var result = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy using Scenario"),
            TestContext.CancellationToken);
        Assert.AreEqual("different", result.Outcome, result.Original.Detail + "; " + result.Edited.Detail);
        Assert.AreEqual("1", result.Original.Result!.Value);
        Assert.AreEqual("2", result.Edited.Result!.Value);
    }
}
