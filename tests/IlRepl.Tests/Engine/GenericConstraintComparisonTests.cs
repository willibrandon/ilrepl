using System.Reflection;
using IlRepl.Engine;
using IlRepl.Host;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Scenario capture compares generic constraints after an edit applies its declared constraint changes.
/// </summary>
[TestClass]
public sealed class GenericConstraintComparisonTests
{
    /// <summary>
    /// Supplies cancellation for actual comparison workers.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Loosened or changed constraints cannot export a scenario that the captured original cannot call.
    /// </summary>
    /// <param name="before">The original generic parameter declaration.</param>
    /// <param name="after">The edited generic parameter declaration.</param>
    /// <param name="argument">A type accepted by the edited method.</param>
    [TestMethod]
    [DataRow("valuetype T", "T", "string")]
    [DataRow("class T", "T", "int32")]
    [DataRow(".ctor T", "T", "string")]
    [DataRow("(IDisposable) T", "(IComparable) T", "string")]
    public void Create_ChangedGenericConstraints_RejectsScenario(string before, string after, string argument)
    {
        var session = IlLines.Load(".class public Choice {", ".method public static int32 Read<" + before + ">() { ldc.i4.s 41; ret }",
            "}");
        var edit = session.PrepareEdit("int32 Choice::Read<[1]>()", "Copy");
        session.CommitEdit(edit.Name, ".method public static int32 Read<" + after + ">() cil managed {\nldc.i4.s 42\nret\n}");
        var definition = (MethodInfo)edit.Method!;
        var selected = definition.MakeGenericMethod(argument == "string" ? typeof(string) : typeof(int));
        Assert.AreEqual(42, selected.Invoke(null, null));
        foreach (var line in IlLines.Expand(".method int32 Scenario() { call Copy<" + argument + ">; ret }"))
        {
            session.AddLine(line);
        }

        var error = Assert.ThrowsExactly<ReplException>(() => ComparisonCapture.Create(session, "Copy using Scenario"));

        Assert.AreEqual("the original and edited signatures must match to compare this method through a scenario", error.Message);
        session.AddLine("call Scenario");
        Assert.AreEqual(42, session.Run().Value);
    }

    /// <summary>
    /// Reordering interface constraints or renaming the parameter preserves a compatible scenario signature.
    /// </summary>
    /// <returns>The completed constraint and worker assertions.</returns>
    [TestMethod]
    public async Task Create_ReorderedConstraints_RemainsCompatible()
    {
        var session = IlLines.Load(".class public Choice {",
            ".method public static int32 Read<(IComparable, IConvertible) T>() { ldc.i4.s 41; ret }", "}");
        var edit = session.PrepareEdit("int32 Choice::Read<[1]>()", "Copy");
        session.CommitEdit(edit.Name, ".method public static int32 Read<(IConvertible, IComparable) U>() cil managed {\n"
            + "ldc.i4.s 42\nret\n}");
        foreach (var line in IlLines.Expand(".method int32 Scenario() { call Copy<string>; ret }"))
        {
            session.AddLine(line);
        }

        var result = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy using Scenario"),
            TestContext.CancellationToken);

        Assert.AreEqual("different", result.Outcome, result.Original.Detail + "; " + result.Edited.Detail);
        Assert.AreEqual("41", result.Original.Result!.Value);
        Assert.AreEqual("42", result.Edited.Result!.Value);
    }
}
