using System.Reflection;
using System.Runtime.Loader;
using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Tests.Shared;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Resource inspection receives a precise blocker before a copied method can observe an empty manifest.
/// </summary>
[TestClass]
public sealed class ManifestResourceEditTests
{
    /// <summary>
    /// Supplies cancellation for real comparison processes.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Real embedded bytes remain available to the original while a blocked draft can be corrected, compared, and exported.
    /// </summary>
    /// <param name="api">The resource inspection API.</param>
    [TestMethod]
    [DataRow("names")]
    [DataRow("info")]
    [DataRow("stream")]
    [DataRow("typed stream")]
    public async Task Edit_ResourceInspectionIsBlockedAndCanBeReplaced(string api)
    {
        var session = new Session();
        var assembly = session.Resolver.LoadImage(ManifestResourceFixture.Create(api));
        Assert.AreSequenceEqual(["Resources.payload"], assembly.GetManifestResourceNames());
        Assert.AreEqual(ResourceLocation.Embedded | ResourceLocation.ContainedInManifestFile,
            assembly.GetManifestResourceInfo("Resources.payload")!.ResourceLocation);
        using (var stream = assembly.GetManifestResourceStream("Resources.payload")!)
        {
            Assert.AreEqual(42, stream.ReadByte());
            Assert.AreEqual(17, stream.ReadByte());
            Assert.AreEqual(255, stream.ReadByte());
            Assert.AreEqual(-1, stream.ReadByte());
        }

        var edit = session.PrepareEdit("int32 [" + assembly.GetName().Name + "]Resources.Owner::Read()", "Copy");
        Assert.AreEqual(42, edit.Original.Requested.Invoke(null, null));
        Assert.HasCount(1, edit.Problems);
        Assert.Contains("GetManifestResource", edit.Problems[0]);
        Assert.Contains("original assembly's resources", edit.Problems[0]);
        Assert.Contains(dependency => dependency.Symbol.Contains("GetManifestResource", StringComparison.Ordinal)
            && dependency.Disposition.StartsWith("blocked:", StringComparison.Ordinal), edit.Dependencies);
        var source = edit.Source;
        Assert.ThrowsExactly<ReplException>(() => session.CommitEdit(edit.Name, source));
        Assert.AreEqual(source, edit.Source);
        Assert.IsNull(edit.Method);
        session.CommitEdit(edit.Name, ".method public static int32 Read() {\nldc.i4.s 42\nret\n}");
        var result = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy ()"),
            TestContext.CancellationToken);
        Assert.AreEqual("match", result.Outcome, result.Original.Detail + "; " + result.Edited.Detail);
        Assert.AreEqual("42", result.Original.Result!.Value);
        Assert.AreEqual("42", result.Edited.Result!.Value);
        var previous = edit.Method;
        Assert.ThrowsExactly<ReplException>(() => session.CommitEdit(edit.Name, source));
        Assert.AreSame(previous, edit.Method);
        Assert.AreEqual(1, edit.Revision);
        session.AddLine("call Copy");
        foreach (var image in new[] { AssemblyExporter.Write(session, "resource-copy"), IlasmLocator.Assemble(session.ToIlAsm()) })
        {
            var context = new AssemblyLoadContext("resource-copy", isCollectible: true);
            try
            {
                var saved = context.LoadImage(image);
                Assert.AreEqual(42, saved.GetType("IlRepl.Cell")!.GetMethod("Run")!.Invoke(null, null));
            }
            finally
            {
                context.Unload();
            }
        }
    }

    /// <summary>
    /// A method token inspects metadata without reading resources, and unrelated methods keep their ordinary behavior.
    /// </summary>
    [TestMethod]
    public void Edit_ResourceMetadataTokensAndLookalikeMethodsRemainUsable()
    {
        var session = IlLines.Load(".class public UserAssembly {",
            ".method public static int32 GetManifestResourceNames() { ldc.i4.s 42; ret }",
            ".method public static int32 Read() { call int32 UserAssembly::GetManifestResourceNames(); ret }", "}",
            ".method public static string Inspect() {",
            "ldtoken method instance string[] Assembly::GetManifestResourceNames()",
            "call class MethodBase MethodBase::GetMethodFromHandle(valuetype RuntimeMethodHandle)",
            "callvirt instance string MemberInfo::get_Name()", "ret", "}");
        foreach (var (reference, name, expected) in new (string, string, object)[]
        {
            ("int32 UserAssembly::Read()", "ReadCopy", 42),
            ("Inspect", "InspectCopy", "GetManifestResourceNames"),
        })
        {
            var edit = session.PrepareEdit(reference, name);
            Assert.IsEmpty(edit.Problems);
            session.CommitEdit(edit.Name, edit.Source);
            Assert.AreEqual(expected, edit.Original.Requested.Invoke(null, null));
            Assert.AreEqual(expected, edit.Method!.Invoke(null, null));
        }
    }
}
