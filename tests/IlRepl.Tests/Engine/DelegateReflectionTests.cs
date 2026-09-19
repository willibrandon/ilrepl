using System.Reflection;
using System.Runtime.Loader;
using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Tests.Shared;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Name-based delegate binding survives edits, real comparisons, and standalone exports.
/// </summary>
[TestClass]
public sealed class DelegateReflectionTests
{
    /// <summary>
    /// Supplies cancellation for the comparison processes.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Delegate factories retain public non-virtual targets without a static reference to those methods.
    /// </summary>
    /// <param name="staticTarget">Whether the named target is static.</param>
    /// <param name="options">The number of delegate-binding options supplied.</param>
    [TestMethod]
    [DataRow(false, 0)]
    [DataRow(false, 1)]
    [DataRow(false, 2)]
    [DataRow(true, 0)]
    [DataRow(true, 1)]
    [DataRow(true, 2)]
    public async Task Compare_NameBoundDelegatesRetainTheirTargets(bool staticTarget, int options)
    {
        var session = IlLines.Load(DelegateReflectionExamples.Source(staticTarget, options).Split('\n'));
        var edit = session.PrepareEdit(DelegateReflectionExamples.Reference(staticTarget), "Copy");
        Assert.IsEmpty(edit.Problems, string.Join('\n', edit.Problems));
        session.CommitEdit(edit.Name, edit.Source);
        Assert.AreEqual(42, Invoke(edit.OriginalMethod, staticTarget));
        Assert.AreEqual(42, Invoke(edit.Method!, staticTarget));
        Assert.Contains(dependency => dependency.Symbol.Contains("Hidden", StringComparison.Ordinal)
            && dependency.Location.Contains("reflective access", StringComparison.Ordinal), edit.Dependencies);
        session.CommitEdit(edit.Name, DelegateReflectionExamples.Method(staticTarget, options, true));
        foreach (var line in DelegateReflectionExamples.Scenario(staticTarget).Split('\n'))
        {
            session.AddLine(line);
        }

        var result = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy using Scenario"),
            TestContext.CancellationToken);
        Assert.AreEqual("different", result.Outcome, result.Original.Detail + "; " + result.Edited.Detail);
        Assert.AreEqual("42", result.Original.Result!.Value);
        Assert.AreEqual("43", result.Edited.Result!.Value);
        session.AddLine("call Scenario");
        foreach (var image in new[] { AssemblyExporter.Write(session, "named-delegate"), IlasmLocator.Assemble(session.ToIlAsm()) })
        {
            var context = new AssemblyLoadContext("named-delegate", isCollectible: true);
            try
            {
                var assembly = context.LoadFromStream(new MemoryStream(image));
                Assert.AreEqual(43, assembly.GetType("IlRepl.Cell")!.GetMethod("Run")!.Invoke(null, null));
                var hidden = assembly.GetType(edit.Method!.DeclaringType!.FullName!)!.GetMethod("Hidden")!;
                Assert.IsTrue(hidden.IsPublic);
                Assert.IsFalse(hidden.IsVirtual);
                Assert.AreEqual(staticTarget, hidden.IsStatic);
            }
            finally
            {
                context.Unload();
            }
        }
    }

    private static object? Invoke(MethodBase method, bool staticTarget) => staticTarget
        ? method.Invoke(null, [typeof(Func<int>), method.DeclaringType])
        : method.Invoke(Activator.CreateInstance(method.DeclaringType!), [typeof(Func<int>)]);
}
