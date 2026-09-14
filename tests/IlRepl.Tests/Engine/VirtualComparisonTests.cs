using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Tests.Shared;
using Mono.Cecil;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Comparison instrumentation records the selected implementation only when real virtual dispatch reaches it.
/// </summary>
[TestClass]
public sealed class VirtualComparisonTests
{
    /// <summary>
    /// Supplies cancellation for real comparison processes.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// A constrained interface override writes to the struct's original storage without recording the unused default method.
    /// </summary>
    [TestMethod]
    public async Task Compare_ConstrainedInterfaceOverridePreservesStructStorage()
    {
        var session = IlLines.Load(VirtualComparisonExamples.Source(false, true).Split('\n'));
        var edit = session.PrepareEdit(VirtualComparisonExamples.Reference(false), "Copy");
        session.CommitEdit(edit.Name, VirtualComparisonExamples.Method(false, true));
        foreach (var line in VirtualStructScenario.Source.Split('\n')) session.AddLine(line);
        session.AddLine("call Scenario");
        Assert.AreEqual(14, session.Run().Value);
        var result = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy using Scenario"),
            TestContext.CancellationToken);
        Assert.AreEqual("incomplete", result.Outcome);
        foreach (var side in new[] { result.Original, result.Edited })
        {
            Assert.IsNull(side.Exception, side.Exception?.Message);
            Assert.AreEqual("14", side.Result!.Value);
            Assert.IsEmpty(side.Invocations);
        }
    }

    /// <summary>
    /// Overrides retain their results and base calls retain their own observed boundaries without adding reflected owner members.
    /// </summary>
    /// <param name="kind">The scenario's call instruction or delegate binding.</param>
    /// <param name="behavior">The derived method's dispatch behavior.</param>
    /// <param name="generic">Whether the owner and selected method are generic.</param>
    [TestMethod]
    [DataRow("virtual", "override", false)]
    [DataRow("constrained", "override", false)]
    [DataRow("delegate", "override", false)]
    [DataRow("virtual", "base", false)]
    [DataRow("constrained", "base", false)]
    [DataRow("delegate", "base", false)]
    [DataRow("virtual", "inherit", false)]
    [DataRow("constrained", "inherit", false)]
    [DataRow("delegate", "inherit", false)]
    [DataRow("virtual", "newslot", false)]
    [DataRow("virtual", "explicit", false)]
    [DataRow("call", "override", false)]
    [DataRow("virtual", "override", true)]
    [DataRow("constrained", "override", true)]
    [DataRow("delegate", "override", true)]
    [DataRow("virtual", "base", true)]
    [DataRow("constrained", "base", true)]
    [DataRow("delegate", "base", true)]
    [DataRow("virtual", "inherit", true)]
    [DataRow("virtual", "newslot", true)]
    [DataRow("call", "override", true)]
    [DataRow("virtual", "explicit", true)]
    [DataRow("virtual", "same", false)]
    [DataRow("delegate", "same", true)]
    [DataRow("virtual", "interface-inherit", false)]
    [DataRow("constrained", "interface-inherit", false)]
    [DataRow("delegate", "interface-inherit", false)]
    [DataRow("virtual", "interface-override", false)]
    [DataRow("constrained", "interface-override", false)]
    [DataRow("delegate", "interface-override", false)]
    [DataRow("virtual", "interface-explicit", false)]
    [DataRow("virtual", "interface-inherit", true)]
    [DataRow("virtual", "interface-override", true)]
    public async Task Compare_VirtualDispatchReachesTheActualImplementation(string kind, string behavior, bool generic)
    {
        var interfaceType = behavior.StartsWith("interface-", StringComparison.Ordinal);
        var session = IlLines.Load(VirtualComparisonExamples.Source(generic, interfaceType).Split('\n'));
        var edit = session.PrepareEdit(VirtualComparisonExamples.Reference(generic), "Copy");
        session.CommitEdit(edit.Name, VirtualComparisonExamples.Method(generic, true));
        foreach (var line in VirtualComparisonExamples.Scenario(kind, behavior, generic).Split('\n')) session.AddLine(line);
        if (interfaceType) behavior = behavior[10..];
        var selected = kind == "call" || behavior is "base" or "inherit" or "newslot";
        var added = kind != "call" && behavior == "base" ? 100 : 0;
        var overridden = behavior == "same" ? 8 : 107;
        session.AddLine("call Scenario");
        Assert.AreEqual(selected ? 9 + added : overridden, session.Run().Value);
        var package = ComparisonCapture.Create(session, "Copy using Scenario");
        foreach (var image in new[] { package.Original, package.Edited })
        {
            using var module = ModuleDefinition.ReadModule(new MemoryStream(image.Image));
            var owner = module.GetTypes().Single(type => type.Namespace == "IlRepl.Edits.Copy"
                && type.Name == (generic ? "Owner`1" : "Owner"));
            Assert.AreSequenceEqual(interfaceType ? ["Read"] : [".ctor", "Read"], owner.Methods.Select(method => method.Name).Order());
        }

        var result = await ProcessComparisonRunner.RunAsync(package, TestContext.CancellationToken);
        Assert.AreEqual(selected ? "different" : "incomplete", result.Outcome, result.Original.Detail + "; " + result.Edited.Detail);
        foreach (var (side, expected) in new[] { (result.Original, 8), (result.Edited, 9) })
        {
            Assert.IsNull(side.Exception, side.Exception?.Message);
            Assert.AreEqual((selected ? expected + added : overridden).ToString(), side.Result!.Value);
            Assert.HasCount(selected ? 1 : 0, side.Invocations);
            if (selected)
            {
                Assert.AreEqual("completed", side.Outcome);
                Assert.AreEqual(expected.ToString(), side.Invocations[0].Outputs.Single(member => member.Name == "return").Value.Value);
            }
            else
            {
                Assert.AreEqual("the scenario did not invoke the selected method", side.Detail);
            }
        }
    }
}
