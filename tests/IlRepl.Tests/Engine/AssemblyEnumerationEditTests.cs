using System.Runtime.Loader;
using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Tests.Shared;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Assembly-wide type discovery receives a precise blocker before a copy can run with an incomplete type set.
/// </summary>
[TestClass]
public sealed class AssemblyEnumerationEditTests
{
    /// <summary>
    /// Supplies cancellation for real comparison processes.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// The actual original finds an unreferenced type, while preflight keeps the blocked draft available for correction.
    /// </summary>
    /// <param name="api">The assembly or module enumeration API.</param>
    [TestMethod]
    [DataRow("GetTypes")]
    [DataRow("GetExportedTypes")]
    [DataRow("DefinedTypes")]
    [DataRow("ExportedTypes")]
    [DataRow("Module.GetTypes")]
    [DataRow("Module.FindTypes")]
    public async Task Edit_AssemblyEnumerationIsBlockedAndCanBeReplaced(string api)
    {
        var session = IlLines.Load(AssemblyEnumerationExamples.Source(api).Split('\n'));
        var edit = session.PrepareEdit("int32 Owner::Read()", "Copy");
        Assert.AreEqual(42, edit.Original.Requested.Invoke(null, null));
        Assert.HasCount(1, edit.Problems);
        Assert.Contains("original assembly's complete type set", edit.Problems[0]);
        Assert.Contains("Read", edit.Problems[0]);
        Assert.Contains(dependency => dependency.Disposition.StartsWith("blocked:", StringComparison.Ordinal), edit.Dependencies);
        var source = edit.Source;
        Assert.ThrowsExactly<ReplException>(() => session.CommitEdit(edit.Name, source));
        Assert.AreEqual(source, edit.Source);
        Assert.AreEqual(0, edit.Revision);
        Assert.IsNull(edit.Method);
        session.CommitEdit(edit.Name, ".method public static int32 Read() {\nldc.i4.s 42\nret\n}");
        Assert.IsEmpty(edit.Problems);
        var result = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy ()"),
            TestContext.CancellationToken);
        Assert.AreEqual("match", result.Outcome, result.Original.Detail + "; " + result.Edited.Detail);
        Assert.AreEqual("42", result.Original.Result!.Value);
        Assert.AreEqual("42", result.Edited.Result!.Value);
        var method = edit.Method;
        Assert.ThrowsExactly<ReplException>(() => session.CommitEdit(edit.Name, source));
        Assert.AreSame(method, edit.Method);
        Assert.AreEqual(1, edit.Revision);
        session.AddLine("call Copy");
        var images = new[] { AssemblyExporter.Write(session, "enumeration-replacement"), IlasmLocator.Assemble(session.ToIlAsm()) };
        foreach (var image in images)
        {
            var context = new AssemblyLoadContext("enumeration-replacement", isCollectible: true);
            try
            {
                var assembly = context.LoadImage(image);
                Assert.AreEqual(42, assembly.GetType("IlRepl.Cell")!.GetMethod("Run")!.Invoke(null, null));
            }
            finally
            {
                context.Unload();
            }
        }
    }

    /// <summary>
    /// An unrelated user method with an enumeration API's name remains a normal copyable dependency.
    /// </summary>
    [TestMethod]
    public void Edit_DoesNotRejectLookalikeEnumerationMethods()
    {
        var session = IlLines.Load(".class public UserAssembly {",
            ".method public static int32 GetTypes() { ldc.i4.s 42; ret }",
            ".method public static int32 Read() { call int32 UserAssembly::GetTypes(); ret }", "}");
        var edit = session.PrepareEdit("int32 UserAssembly::Read()", "Copy");
        Assert.IsEmpty(edit.Problems);
        session.CommitEdit(edit.Name, edit.Source);
        Assert.AreEqual(42, edit.Original.Requested.Invoke(null, null));
        Assert.AreEqual(42, edit.Method!.Invoke(null, null));
    }

    /// <summary>
    /// Inspecting an enumeration method's metadata token does not enumerate or depend on the copied assembly's types.
    /// </summary>
    [TestMethod]
    public void Edit_EnumerationMetadataTokenRemainsUsable()
    {
        var session = IlLines.Load(".method public static string Read() {",
            "ldtoken method instance class Type[] Assembly::GetTypes()",
            "call class MethodBase MethodBase::GetMethodFromHandle(valuetype RuntimeMethodHandle)",
            "callvirt instance string MemberInfo::get_Name()", "ret", "}");
        var edit = session.PrepareEdit("Read", "Copy");
        Assert.IsEmpty(edit.Problems);
        session.CommitEdit(edit.Name, edit.Source);
        Assert.AreEqual("GetTypes", edit.Original.Requested.Invoke(null, null));
        Assert.AreEqual("GetTypes", edit.Method!.Invoke(null, null));
    }
}
