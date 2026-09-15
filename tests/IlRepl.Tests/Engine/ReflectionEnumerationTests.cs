using System.Reflection;
using System.Runtime.Loader;
using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Tests.Shared;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Generated aliases and observation helpers preserve the copied owner's reflected method set.
/// </summary>
[TestClass]
public sealed class ReflectionEnumerationTests
{
    /// <summary>
    /// Supplies cancellation for real comparison processes.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Original execution, edits, comparisons, and exports all enumerate the same declared methods.
    /// </summary>
    /// <param name="instance">Whether the selected member has a receiver.</param>
    /// <param name="privateMethod">Whether the selected member is private.</param>
    /// <param name="generic">Whether its owner and method are generic.</param>
    [TestMethod]
    [DataRow(false, false, false)]
    [DataRow(false, true, false)]
    [DataRow(true, false, false)]
    [DataRow(true, true, false)]
    [DataRow(false, false, true)]
    [DataRow(false, true, true)]
    [DataRow(true, false, true)]
    [DataRow(true, true, true)]
    public async Task Compare_EnumerationPreservesTheOriginalMemberSet(bool instance, bool privateMethod, bool generic)
    {
        var session = IlLines.Load(ReflectionEnumerationExamples.Source(instance, privateMethod, generic).Split('\n'));
        var edit = session.PrepareEdit(ReflectionEnumerationExamples.Reference(instance, generic), "Copy");
        Assert.IsEmpty(edit.Problems, string.Join('\n', edit.Problems));
        session.CommitEdit(edit.Name, edit.Source);
        Assert.AreEqual(2, Invoke(edit.OriginalMethod));
        Assert.AreEqual(2, Invoke(edit.Method!));
        AssertMembers(edit.Method!.DeclaringType!);
        session.CommitEdit(edit.Name, ReflectionEnumerationExamples.Method(instance, privateMethod, generic, true));
        foreach (var line in ReflectionEnumerationExamples.Scenario(instance, generic).Split('\n'))
        {
            session.AddLine(line);
        }

        var result = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy using Scenario"),
            TestContext.CancellationToken);
        Assert.AreEqual("different", result.Outcome, result.Original.Detail + "; " + result.Edited.Detail);
        Assert.AreEqual("2", result.Original.Result!.Value);
        Assert.AreEqual("3", result.Edited.Result!.Value);
        session.AddLine("call Scenario");
        foreach (var image in new[] { AssemblyExporter.Write(session, "enumerated-copy"), IlasmLocator.Assemble(session.ToIlAsm()) })
        {
            var context = new AssemblyLoadContext("enumeration-export", isCollectible: true);
            try
            {
                var assembly = context.LoadFromStream(new MemoryStream(image));
                Assert.AreEqual(3, assembly.GetType("IlRepl.Cell")!.GetMethod("Run")!.Invoke(null, null));
                var owner = assembly.GetType(generic ? "IlRepl.Edits.Copy.Owner`1" : "IlRepl.Edits.Copy.Owner")!;
                AssertMembers(owner);
            }
            finally
            {
                context.Unload();
            }
        }
    }

    private static object? Invoke(MethodBase method) =>
        method.Invoke(method.IsStatic ? null : Activator.CreateInstance(method.DeclaringType!), null);

    private static void AssertMembers(Type owner) => Assert.AreSequenceEqual(["Hidden", "Read"],
        owner.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static
            | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(method => method.Name).Order(StringComparer.Ordinal));
}
