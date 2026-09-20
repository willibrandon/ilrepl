using System.Reflection;
using System.Runtime.Loader;
using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Tests.Shared;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Assembly and module attribute inspection retains a recoverable draft instead of observing generated assembly metadata.
/// </summary>
[TestClass]
public sealed class AssemblyAttributeInspectionTests
{
    private const string Problem = "assembly and module attribute inspection cannot reproduce the original metadata";
    private static readonly string[] Targets = ["Assembly", "Module", "Type", "Member"];
    private static readonly string[] Dispatches = ["instance", "attribute", "extensions", "data", "provider"];

    /// <summary>
    /// Supplies cancellation for actual comparison processes.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Covers every actual BCL overload, including generic extensions and interface calls with unknown metadata targets.
    /// </summary>
    public static IEnumerable<object[]> InspectionCases => Targets
        .SelectMany(target => Dispatches
            .Where(dispatch => target is "Assembly" or "Module" || dispatch == "provider")
            .SelectMany(dispatch => AssemblyAttributeFixture.Apis(target, dispatch).Select((api, index) =>
                new object[] { target, dispatch, index, api.ToString()! })));

    /// <summary>
    /// Ensures the dynamic matrix includes every required API family instead of silently producing no matching tests.
    /// </summary>
    [TestMethod]
    public void Inventory_ContainsAllRequiredReflectionShapes()
    {
        foreach (var target in new[] { "Assembly", "Module" })
        {
            var instance = AssemblyAttributeFixture.Apis(target, "instance");
            Assert.HasCount(5, instance);
            Assert.HasCount(2, instance.Where(api => api.Name == "GetCustomAttributes"));
            foreach (var name in new[] { "IsDefined", "GetCustomAttributesData", "get_CustomAttributes" })
            {
                Assert.Contains(api => api.Name == name, instance);
            }

            Assert.HasCount(3, AssemblyAttributeFixture.Apis(target, "provider"));
            Assert.HasCount(1, AssemblyAttributeFixture.Apis(target, "data"));
            foreach (var dispatch in new[] { "attribute", "extensions" })
            {
                var apis = AssemblyAttributeFixture.Apis(target, dispatch);
                foreach (var name in new[] { "GetCustomAttribute", "GetCustomAttributes", "IsDefined" })
                {
                    Assert.Contains(api => api.Name == name, apis);
                }
            }

            var generic = AssemblyAttributeFixture.Apis(target, "extensions").Where(api => api.IsGenericMethod).ToArray();
            Assert.HasCount(2, generic);
            Assert.Contains(api => api.Name == "GetCustomAttribute", generic);
            Assert.Contains(api => api.Name == "GetCustomAttributes", generic);
        }
    }

    /// <summary>
    /// Every inspection runs against actual metadata, rejects atomically, and accepts a corrected executable in fresh workers and exports.
    /// </summary>
    /// <param name="target">The metadata receiver.</param>
    /// <param name="dispatch">The reflection dispatch family.</param>
    /// <param name="index">The selected overload's stable index.</param>
    /// <param name="signature">The exact runtime signature recorded in the test display.</param>
    /// <returns>The completed recovery, comparison, and independent export assertions.</returns>
    [TestMethod]
    [DynamicData(nameof(InspectionCases))]
    public async Task Edit_AllInspectionOverloadsRetainRecoverableDraft(string target, string dispatch, int index, string signature)
    {
        var api = AssemblyAttributeFixture.Apis(target, dispatch)[index];
        Assert.AreEqual(signature, api.ToString());
        var session = new Session();
        var assembly = session.Resolver.LoadImage(AssemblyAttributeFixture.Create(target, api));
        Assert.IsTrue(assembly.GetCustomAttribute<CLSCompliantAttribute>()!.IsCompliant);
        Assert.IsTrue(assembly.ManifestModule.GetCustomAttribute<CLSCompliantAttribute>()!.IsCompliant);
        var edit = session.PrepareEdit("int32 [" + assembly.GetName().Name + "]AttributeInspection.Owner::Read()", "Copy");
        Assert.AreEqual(42, edit.Original.Requested.Invoke(null, null));
        Assert.HasCount(1, edit.Problems, string.Join("; ", edit.Problems));
        Assert.Contains(Problem, edit.Problems[0]);
        Assert.Contains(api.Name, edit.Problems[0]);
        Assert.Contains(dependency => dependency.Symbol.Contains(api.Name, StringComparison.Ordinal)
            && dependency.Disposition.Contains(Problem, StringComparison.Ordinal), edit.Dependencies);
        var source = edit.Source;
        var revision = session.CompletionRevision;
        var error = Assert.ThrowsExactly<ReplException>(() => session.CommitEdit(edit.Name, source));
        Assert.Contains(Problem, error.Message);
        Assert.AreEqual(source, edit.Source);
        Assert.AreEqual(0, edit.Revision);
        Assert.AreEqual(revision, session.CompletionRevision);
        Assert.IsNull(edit.Method);
        session.CommitEdit(edit.Name, ".method public static int32 Read() {\nldc.i4.s 42\nret\n}");
        Assert.IsEmpty(edit.Problems);
        Assert.AreEqual(42, edit.Method!.Invoke(null, null));
        var previous = edit.Method;
        var corrected = edit.Source;
        revision = session.CompletionRevision;
        error = Assert.ThrowsExactly<ReplException>(() => session.CommitEdit(edit.Name, source));
        Assert.Contains(Problem, error.Message);
        Assert.AreSame(previous, edit.Method);
        Assert.AreEqual(corrected, edit.Source);
        Assert.AreEqual(1, edit.Revision);
        Assert.AreEqual(revision, session.CompletionRevision);
        var result = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy ()"),
            TestContext.CancellationToken);
        Assert.AreEqual("match", result.Outcome, result.Original.Detail + "; " + result.Edited.Detail);
        foreach (var side in new[] { result.Original, result.Edited })
        {
            Assert.AreEqual("completed", side.Outcome, side.Detail);
            Assert.IsNull(side.Exception);
            Assert.AreEqual("42", side.Result!.Value);
            Assert.HasCount(1, side.Invocations);
        }

        session.AddLine("call Copy");
        foreach (var image in new[] { AssemblyExporter.Write(session, "attribute-copy"), IlasmLocator.Assemble(session.ToIlAsm()) })
        {
            AssertExport(image);
        }
    }

    /// <summary>
    /// Tokens for every blocked API remain legal metadata references without performing attribute inspection.
    /// </summary>
    /// <param name="target">The metadata target type.</param>
    /// <param name="dispatch">The API dispatch family.</param>
    [TestMethod]
    [DataRow("Assembly", "instance")]
    [DataRow("Module", "instance")]
    [DataRow("Assembly", "attribute")]
    [DataRow("Module", "attribute")]
    [DataRow("Assembly", "extensions")]
    [DataRow("Module", "extensions")]
    [DataRow("Assembly", "data")]
    [DataRow("Module", "data")]
    [DataRow("Assembly", "provider")]
    [DataRow("Module", "provider")]
    public void Edit_MetadataTokensRemainSupported(string target, string dispatch)
    {
        foreach (var api in AssemblyAttributeFixture.Apis(target, dispatch))
        {
            var session = new Session();
            var assembly = session.Resolver.LoadImage(AssemblyAttributeFixture.Create(target, api, metadataOnly: true));
            var edit = session.PrepareEdit("int32 [" + assembly.GetName().Name + "]AttributeInspection.Owner::Read()", "Copy");
            Assert.IsEmpty(edit.Problems, api + ": " + string.Join("; ", edit.Problems));
            session.CommitEdit(edit.Name, edit.Source);
            Assert.AreEqual(42, edit.Original.Requested.Invoke(null, null));
            Assert.AreEqual(42, edit.Method!.Invoke(null, null));
        }
    }

    /// <summary>
    /// Direct type and member reflection continues to observe copied attributes through each supported BCL dispatch family.
    /// </summary>
    /// <param name="target">Type or Member.</param>
    /// <param name="dispatch">The API dispatch family.</param>
    [TestMethod]
    [DataRow("Type", "instance")]
    [DataRow("Member", "instance")]
    [DataRow("Type", "attribute")]
    [DataRow("Member", "attribute")]
    [DataRow("Type", "extensions")]
    [DataRow("Member", "extensions")]
    [DataRow("Type", "data")]
    [DataRow("Member", "data")]
    public void Edit_TypeAndMemberAttributesRemainSupported(string target, string dispatch)
    {
        foreach (var api in AssemblyAttributeFixture.Apis(target, dispatch))
        {
            var session = new Session();
            var assembly = session.Resolver.LoadImage(AssemblyAttributeFixture.Create(target, api));
            var edit = session.PrepareEdit("int32 [" + assembly.GetName().Name + "]AttributeInspection.Owner::Read()", "Copy");
            Assert.IsEmpty(edit.Problems, api + ": " + string.Join("; ", edit.Problems));
            session.CommitEdit(edit.Name, edit.Source);
            Assert.AreEqual(42, edit.Original.Requested.Invoke(null, null));
            Assert.AreEqual(42, edit.Method!.Invoke(null, null));
        }
    }

    /// <summary>
    /// User-defined methods with the same names and an Assembly parameter retain their ordinary executable behavior.
    /// </summary>
    /// <param name="name">The lookalike API name.</param>
    [TestMethod]
    [DataRow("GetCustomAttributes")]
    [DataRow("GetCustomAttribute")]
    [DataRow("GetCustomAttributesData")]
    [DataRow("IsDefined")]
    [DataRow("get_CustomAttributes")]
    public void Edit_UserLookalikeMethodsRemainSupported(string name)
    {
        var session = IlLines.Load(".class public UserAssembly {", ".method public static int32 " + name + "(class Assembly value) {",
            "ldc.i4.s 42", "ret", "}", ".method public static int32 Read() {", "call class Assembly Assembly::GetExecutingAssembly()",
            "call int32 UserAssembly::" + name + "(class Assembly)", "ret", "}", "}");
        var edit = session.PrepareEdit("int32 UserAssembly::Read()", "Copy");
        Assert.IsEmpty(edit.Problems);
        session.CommitEdit(edit.Name, edit.Source);
        Assert.AreEqual(42, edit.Original.Requested.Invoke(null, null));
        Assert.AreEqual(42, edit.Method!.Invoke(null, null));
    }

    private static void AssertExport(byte[] image)
    {
        var context = new AssemblyLoadContext("attribute-copy", isCollectible: true);
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
