using System.Runtime.Loader;
using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Tests.Shared;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Exact external signatures report nominal identity conflicts before emission and retain compatible external references.
/// </summary>
[TestClass]
public sealed class ExactBoundaryTests
{
    /// <summary>
    /// Supplies cancellation for real comparison workers.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Copied types hidden in modifiers and function pointers are diagnosed while equivalent external signatures remain callable.
    /// </summary>
    /// <param name="kind">The exact signature shape at the external boundary.</param>
    [TestMethod]
    [DataRow("parameter-modifier")]
    [DataRow("parameter-pointer-modifier")]
    [DataRow("parameter-function-return")]
    [DataRow("parameter-function-parameter")]
    [DataRow("parameter-function-modifier")]
    [DataRow("return-modifier")]
    [DataRow("return-function")]
    [DataRow("field-modifier")]
    [DataRow("field-function")]
    [DataRow("field-array-modifier")]
    public async Task Edit_ExactExternalBoundariesAreValidatedBeforeEmission(string kind)
    {
        foreach (var copiedType in new[] { true, false })
        {
            var session = new Session();
            var original = ExactBoundaryFixture.Create(kind, copiedType);
            var assembly = session.Resolver.LoadImage(original);
            Assert.AreEqual(42, assembly.GetType("Owner")!.GetMethod("Read")!.Invoke(null, null));
            var edit = session.PrepareEdit("int32 [" + assembly.GetName().Name + "]Owner::Read()", "Copy");
            if (copiedType)
            {
                Assert.HasCount(1, edit.Problems);
                var memberKind = kind.StartsWith("field", StringComparison.Ordinal) ? "external field" : "external member";
                Assert.Contains(memberKind, edit.Problems[0]);
                Assert.Contains("original nominal type", edit.Problems[0]);
                Assert.DoesNotContain("runtime rejected", edit.Problems[0]);
                Assert.IsNull(edit.Method);
            }
            else
            {
                Assert.IsEmpty(edit.Problems, string.Join('\n', edit.Problems));
                session.CommitEdit(edit.Name, edit.Source);
                Assert.AreEqual(42, edit.Method!.Invoke(null, null));
                session.AddLine("call Copy");
                foreach (var image in new[] { AssemblyExporter.Write(session, "exact-boundary"), IlasmLocator.Assemble(session.ToIlAsm()) })
                {
                    var context = new AssemblyLoadContext("exact-boundary", isCollectible: true);
                    try
                    {
                        context.LoadFromStream(new MemoryStream(original));
                        var exported = context.LoadFromStream(new MemoryStream(image));
                        Assert.AreEqual(42, exported.GetType("IlRepl.Cell")!.GetMethod("Run")!.Invoke(null, null));
                    }
                    finally
                    {
                        context.Unload();
                    }
                }
            }

            session.CommitEdit(edit.Name, ".method public static int32 Read() {\nldc.i4.s 43\nret\n}");
            Assert.IsEmpty(edit.Problems, string.Join('\n', edit.Problems));
            var result = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy ()"),
                TestContext.CancellationToken);
            Assert.AreEqual("different", result.Outcome, result.Original.Detail + "; " + result.Edited.Detail);
            Assert.AreEqual("42", result.Original.Result!.Value);
            Assert.AreEqual("43", result.Edited.Result!.Value);
        }
    }
}
