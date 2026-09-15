using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Protocol;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Managed reference observations distinguish shared storage from independent storage containing equal values.
/// </summary>
[TestClass]
public sealed class MethodComparisonAliasTests
{
    private static readonly string[] ScenarioPrefix =
    [
        ".method int32 Scenario() {", ".locals init (int32 left, int32 right, int32& chosen)",
        "ldc.i4 41", "stloc.0", "ldc.i4 41", "stloc.1",
    ];

    /// <summary>
    /// The cancellation token for real comparison worker processes.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// A scenario records both repeated and distinct reference arguments without confusing equal scalar values.
    /// </summary>
    [TestMethod]
    public async Task Run_ScenarioPreservesSameAndDistinctReferenceArguments()
    {
        var session = CreateChoice(change: false);
        AddScenario(session, "ldloca.s 0", "ldloca.s 0", "call int32& Copy(int32&, int32&)", "pop",
            "ldloca.s 0", "ldloca.s 1", "call int32& Copy(int32&, int32&)", "ldind.i4", "ret");

        var result = await Run(session, "Copy using Scenario");

        Assert.AreEqual("match", result.Outcome, Details(result));
        foreach (var side in new[] { result.Original, result.Edited })
        {
            Assert.AreEqual("41", side.Result!.Value);
            Assert.HasCount(2, side.Invocations);
            AssertAliases(side.Invocations[0].Inputs, "-1,1,1");
            AssertAliases(side.Invocations[0].Outputs, "-1,1,1,1");
            AssertAliases(side.Invocations[1].Inputs, "-1,1,2");
            AssertAliases(side.Invocations[1].Outputs, "-1,1,2,1");
        }
    }

    /// <summary>
    /// Returning a different argument reference is an output difference even when all observed scalar values match.
    /// </summary>
    [TestMethod]
    public async Task Run_EqualValuesWithDifferentReturnAliasesAreDifferentOutputs()
    {
        var session = CreateChoice(change: true);

        var result = await Run(session, "Copy (41, 41)");

        Assert.AreEqual("different", result.Outcome, Details(result));
        Assert.AreEqual("41", result.Original.Result!.Value);
        Assert.AreEqual("41", result.Edited.Result!.Value);
        var before = result.Original.Invocations.Single();
        var after = result.Edited.Invocations.Single();
        AssertAliases(before.Inputs, "-1,1,2");
        AssertAliases(after.Inputs, "-1,1,2");
        AssertAliases(before.Outputs, "-1,1,2,1");
        AssertAliases(after.Outputs, "-1,1,2,2");
        Assert.AreSequenceEqual(Scalars(before.Outputs), Scalars(after.Outputs));
    }

    /// <summary>
    /// A changed return alias that changes the next invocation's storage relationships reports different inputs.
    /// </summary>
    [TestMethod]
    public async Task Run_ChangedArgumentAliasesAreClassifiedAsDifferentInputs()
    {
        var session = CreateChoice(change: true);
        AddScenario(session, "ldloca.s 0", "ldloca.s 1", "call int32& Copy(int32&, int32&)", "stloc.2",
            "ldloc.2", "ldloca.s 0", "call int32& Copy(int32&, int32&)", "ldind.i4", "ret");

        var result = await Run(session, "Copy using Scenario");

        Assert.AreEqual("different-inputs", result.Outcome, Details(result));
        Assert.AreEqual("41", result.Original.Result!.Value);
        Assert.AreEqual("41", result.Edited.Result!.Value);
        Assert.HasCount(2, result.Original.Invocations);
        Assert.HasCount(2, result.Edited.Invocations);
        AssertAliases(result.Original.Invocations[0].Inputs, "-1,1,2");
        AssertAliases(result.Edited.Invocations[0].Inputs, "-1,1,2");
        AssertAliases(result.Original.Invocations[1].Inputs, "-1,1,1");
        AssertAliases(result.Edited.Invocations[1].Inputs, "-1,1,2");
        Assert.AreSequenceEqual(Scalars(result.Original.Invocations[1].Inputs), Scalars(result.Edited.Invocations[1].Inputs));
    }

    /// <summary>
    /// Returning independent static storage differs from returning an argument even when both contain the same value.
    /// </summary>
    [TestMethod]
    public async Task Run_ReturnedStaticStorageDoesNotAliasEqualArgumentStorage()
    {
        var session = IlLines.Load(".class public Storage {", ".field public static int32 Spare",
            ".method private static void .cctor() { ldc.i4 41; stsfld int32 Storage::Spare; ret }",
            ".method public static int32& Choose(int32& value) { ldarg.0; ret }", "}");
        var edit = session.PrepareEdit("int32& Storage::Choose(int32&)", "Copy");
        session.CommitEdit(edit.Name, edit.Source.Replace("ldarg.0", "ldsflda int32 Storage::Spare", StringComparison.Ordinal));

        var result = await Run(session, "Copy (41)");

        Assert.AreEqual("different", result.Outcome, Details(result));
        Assert.AreEqual("41", result.Original.Result!.Value);
        Assert.AreEqual("41", result.Edited.Result!.Value);
        var before = result.Original.Invocations.Single();
        var after = result.Edited.Invocations.Single();
        AssertAliases(before.Inputs, "-1,1");
        AssertAliases(after.Inputs, "-1,1");
        AssertAliases(before.Outputs, "-1,1,1");
        AssertAliases(after.Outputs, "-1,1,2");
        Assert.AreSequenceEqual(Scalars(before.Outputs), Scalars(after.Outputs));
    }

    private static Session CreateChoice(bool change)
    {
        var session = IlLines.Load(".method int32& Choose(int32& left, int32& right) { ldarg.0; ret }");
        var edit = session.PrepareEdit("Choose", "Copy");
        session.CommitEdit(edit.Name, change ? edit.Source.Replace("ldarg.0", "ldarg.1", StringComparison.Ordinal) : edit.Source);
        return session;
    }

    private static void AddScenario(Session session, params string[] body)
    {
        foreach (var line in ScenarioPrefix.Concat(body).Append("}"))
        {
            session.AddLine(line);
        }
    }

    private Task<ComparisonReply> Run(Session session, string command) =>
        ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, command), TestContext.CancellationToken);

    private static void AssertAliases(IReadOnlyList<ObservedMember> values, string expected)
    {
        var aliases = values.Single(member => member.Name == "reference aliases").Value;
        Assert.AreEqual("scalar", aliases.Kind);
        Assert.AreEqual("managed references", aliases.Type);
        Assert.AreEqual(expected, aliases.Value);
    }

    private static IEnumerable<(string, string, string?)> Scalars(IReadOnlyList<ObservedMember> values) =>
        values.Where(member => member.Name != "reference aliases")
            .Select(member => (member.Name, member.Value.Type, member.Value.Value));

    private static string Details(ComparisonReply result) =>
        $"{result.Outcome}: original={result.Original.Outcome} {result.Original.Detail}; "
        + $"edited={result.Edited.Outcome} {result.Edited.Detail}";
}
