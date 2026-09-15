using System.Runtime.Loader;
using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Tests.Shared;
using Mono.Cecil;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Copied types preserve declared interface rows and the implementation selected by real interface calls.
/// </summary>
[TestClass]
public sealed class InheritedInterfaceEditTests
{
    /// <summary>
    /// Supplies cancellation for real comparison processes.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Inheritance and deliberate reimplementation retain distinct dispatch through revisions and standalone exports.
    /// </summary>
    /// <param name="behavior">The derived type's relationship to the interface.</param>
    /// <param name="generic">Whether the owner and interface are generic.</param>
    /// <param name="expected">The implementation selected by the source declaration.</param>
    [TestMethod]
    [DataRow("inherit", false, 1)]
    [DataRow("inherit", true, 1)]
    [DataRow("reimplement", false, 2)]
    [DataRow("reimplement", true, 2)]
    [DataRow("override", false, 2)]
    [DataRow("override", true, 2)]
    [DataRow("explicit", false, 2)]
    [DataRow("explicit", true, 2)]
    [DataRow("class-explicit", false, 2)]
    [DataRow("class-explicit", true, 2)]
    [DataRow("interface-inherit", false, 1)]
    [DataRow("interface-inherit", true, 1)]
    [DataRow("overload", true, 2)]
    public async Task Compare_CopiedDerivedTypesPreserveInterfaceDispatch(string behavior, bool generic, int expected)
    {
        var session = IlLines.Load(InheritedInterfaceExamples.Source(behavior, generic).Split('\n'));
        var edit = session.PrepareEdit(InheritedInterfaceExamples.Reference(generic), "Copy");
        Assert.AreEqual(expected, edit.Original.Requested.Invoke(null, null));
        Assert.IsEmpty(edit.Problems, string.Join('\n', edit.Problems));
        session.CommitEdit(edit.Name, edit.Source);
        Assert.AreEqual(expected, edit.Method!.Invoke(null, null));
        var unchanged = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy ()"),
            TestContext.CancellationToken);
        Assert.AreEqual("match", unchanged.Outcome, unchanged.Original.Detail + "; " + unchanged.Edited.Detail);
        Assert.AreEqual(expected.ToString(), unchanged.Edited.Result!.Value);
        session.CommitEdit(edit.Name, InheritedInterfaceExamples.Method(behavior, generic, true));
        var changed = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy ()"),
            TestContext.CancellationToken);
        Assert.AreEqual("different", changed.Outcome, changed.Original.Detail + "; " + changed.Edited.Detail);
        Assert.AreEqual(expected.ToString(), changed.Original.Result!.Value);
        Assert.AreEqual((expected + 10).ToString(), changed.Edited.Result!.Value);
        session.AddLine("call Copy");
        foreach (var image in new[] { AssemblyExporter.Write(session, "interface-slots"), IlasmLocator.Assemble(session.ToIlAsm()) })
        {
            using var module = ModuleDefinition.ReadModule(new MemoryStream(image));
            var owner = module.GetTypes().Single(type => type.Namespace == "IlRepl.Edits.Copy"
                && type.Name == (generic ? "Owner`1" : "Owner"));
            Assert.HasCount(behavior is "reimplement" or "explicit" or "class-explicit" ? 1 : 0, owner.Interfaces);
            Assert.HasCount(1, owner.BaseType.Resolve().Interfaces);
            var contract = owner.BaseType.Resolve().Interfaces[0].InterfaceType.Resolve();
            Assert.HasCount(behavior == "interface-inherit" ? 1 : 0, contract.Interfaces);
            var context = new AssemblyLoadContext("interface-slots", isCollectible: true);
            try
            {
                var assembly = context.LoadFromStream(new MemoryStream(image));
                Assert.AreEqual(expected + 10, assembly.GetType("IlRepl.Cell")!.GetMethod("Run")!.Invoke(null, null));
            }
            finally
            {
                context.Unload();
            }
        }
    }
}
