using System.Reflection;
using System.Runtime.Loader;
using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Tests.Shared;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Name-only siblings retain their executable context through copies, revisions, isolated comparisons, and standalone exports.
/// </summary>
[TestClass]
public sealed partial class SiblingTypeLookupTests
{
    /// <summary>
    /// Supplies cancellation for actual comparison workers.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Covers concrete lookup and activation overloads, compound names, independent state, and nontrivial name origins.
    /// </summary>
    /// <param name="api">The actual BCL lookup family.</param>
    /// <param name="shape">The reflected sibling name shape.</param>
    /// <param name="arity">The actual overload parameter count.</param>
    /// <param name="ignoreCase">Whether the supplied name requires case-insensitive matching.</param>
    /// <param name="internalType">Whether the source sibling is internal.</param>
    /// <param name="qualified">Whether the source assembly identity occurs in the lookup arguments.</param>
    /// <param name="flow">The flow of the name into its lookup.</param>
    /// <returns>The completed real execution, comparison, and export assertions.</returns>
    [TestMethod]
    [DataRow("type", "plain", 1, false, false, false, "literal")]
    [DataRow("type", "plain", 2, false, true, true, "local")]
    [DataRow("type", "plain", 3, true, true, true, "return")]
    [DataRow("type", "nested", 3, true, true, true, "argument")]
    [DataRow("type", "generic", 1, false, true, true, "identity")]
    [DataRow("type", "array", 3, true, false, true, "concat")]
    [DataRow("type", "bounded", 1, false, false, false, "literal")]
    [DataRow("type", "matrix", 1, false, true, true, "literal")]
    [DataRow("type", "component", 1, false, false, false, "literal")]
    [DataRow("type", "component", 3, true, true, true, "return")]
    [DataRow("assembly", "plain", 1, false, false, false, "literal")]
    [DataRow("assembly", "plain", 2, false, true, false, "local")]
    [DataRow("assembly", "nested", 3, true, true, false, "return")]
    [DataRow("assembly", "generic", 3, false, false, false, "argument")]
    [DataRow("assembly", "array", 3, true, false, false, "identity")]
    [DataRow("module", "plain", 1, false, true, false, "literal")]
    [DataRow("module", "plain", 2, false, false, false, "concat")]
    [DataRow("module", "nested", 3, true, true, false, "return")]
    [DataRow("module", "generic", 3, false, false, false, "argument")]
    [DataRow("module", "matrix", 3, true, false, false, "local")]
    [DataRow("assembly-create", "plain", 1, false, false, false, "literal")]
    [DataRow("assembly-create", "nested", 2, true, true, false, "return")]
    [DataRow("assembly-create", "generic", 7, true, true, false, "argument")]
    [DataRow("activator", "plain", 2, false, false, false, "literal")]
    [DataRow("activator", "nested", 3, false, true, true, "return")]
    [DataRow("activator", "generic", 8, true, true, true, "argument")]
    public async Task Edit_NameOnlySiblingPreservesContext(string api, string shape, int arity, bool ignoreCase,
        bool internalType, bool qualified, string flow)
        => await RunCaseAsync(api, shape, arity, ignoreCase, internalType, qualified, flow);

    private async Task RunCaseAsync(string api, string shape, int arity, bool ignoreCase, bool internalType,
        bool qualified, string flow, string? directory = null)
    {
        var path = Path.Combine(directory ?? Path.GetTempPath(), "sibling-" + Guid.NewGuid().ToString("N") + ".dll");
        try
        {
            var session = new Session();
            var image = SiblingTypeLookupFixture.Create(api, shape, arity, ignoreCase, internalType, qualified, flow, path);
            if (api == "activator-from") File.WriteAllBytes(path, image);
            var assembly = api == "activator-from" ? session.Resolver.Load(path) : session.Resolver.LoadImage(image);
            var owner = assembly.GetType("Lookup.Owner")!;
            var sibling = assembly.GetType("Lookup.Sibling")!;
            Assert.AreSame(owner.Assembly, sibling.Assembly);
            Assert.IsFalse(sibling.IsNested);
            Assert.AreEqual(!internalType, sibling.IsPublic);
            AssertNoSiblingTokens(owner, session);
            var edit = session.PrepareEdit("int32 [" + assembly.GetName().Name + "]Lookup.Owner::Read()", "Copy");
            Assert.AreEqual(42, edit.Original.Requested.Invoke(null, null));
            Assert.IsEmpty(edit.Problems, string.Join("; ", edit.Problems));
            session.CommitEdit(edit.Name, edit.Source);
            Assert.AreEqual(42, edit.OriginalMethod.Invoke(null, null));
            Assert.AreEqual(42, edit.Method!.Invoke(null, null));
            await AssertComparisonAsync(session, "match", "42");
            var sourceState = StateType(assembly, shape).GetField("State")!;
            var copiedState = StateType(edit.Method.Module.Assembly, shape).GetField("State")!;
            sourceState.SetValue(null, 100);
            Assert.AreEqual(100, edit.Original.Requested.Invoke(null, null));
            Assert.AreEqual(42, edit.OriginalMethod.Invoke(null, null));
            Assert.AreEqual(42, edit.Method.Invoke(null, null));
            copiedState.SetValue(null, 200);
            Assert.AreEqual(200, edit.Method.Invoke(null, null));
            Assert.AreEqual(100, edit.Original.Requested.Invoke(null, null));
            Assert.AreEqual(42, edit.OriginalMethod.Invoke(null, null));
            var source = edit.Source;
            var position = source.LastIndexOf("ret", StringComparison.Ordinal);
            Assert.IsGreaterThanOrEqualTo(0, position);
            session.CommitEdit(edit.Name, source.Insert(position, "ldc.i4.1\nadd\n"));
            Assert.AreEqual(43, edit.Method!.Invoke(null, null));
            Assert.AreEqual(42, edit.OriginalMethod.Invoke(null, null));
            Assert.AreEqual(100, edit.Original.Requested.Invoke(null, null));
            if (shape != "generic")
            {
                var reflected = StateType(edit.Method.Module.Assembly, shape);
                Assert.IsNotNull(reflected.GetNestedType("Initializer", BindingFlags.NonPublic));
            }
            await AssertComparisonAsync(session, "different", "43");
            session.AddLine("call Copy");
            foreach (var exported in new[] { AssemblyExporter.Write(session, "sibling-copy"), IlasmLocator.Assemble(session.ToIlAsm()) })
                AssertExport(exported);
        }
        finally
        {
            if (directory is null && File.Exists(path)) File.Delete(path);
        }
    }

    private static void AssertNoSiblingTokens(Type owner, Session session)
    {
        foreach (var method in owner.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            Assert.IsTrue(method.ReturnType == typeof(int) || method.ReturnType == typeof(string));
            foreach (var parameter in method.GetParameters()) Assert.AreEqual(typeof(string), parameter.ParameterType);
            var listing = MethodDisassembler.Disassemble(method, session);
            Assert.IsEmpty(listing.Problems);
            foreach (var entry in listing.Entries)
            {
                var referenced = entry.Instruction?.Operand switch
                {
                    Type type => type,
                    ResolvedMethod called => called.Method?.DeclaringType,
                    MemberInfo member => member.DeclaringType,
                    _ => null,
                };
                if (referenced?.Assembly == owner.Assembly) Assert.AreEqual(owner, referenced, entry.DisplayText);
            }
        }
    }

    private static Type StateType(Assembly assembly, string shape)
    {
        var type = assembly.GetTypes().Single(type => type.GetField("State") is not null && (shape switch
        {
            "generic" => type.IsGenericTypeDefinition,
            "nested" => type.IsNested && type.Name == "Nested",
            _ => !type.IsNested && !type.IsGenericType,
        }));
        return type.IsGenericTypeDefinition ? type.MakeGenericType(typeof(int)) : type;
    }

    private async Task AssertComparisonAsync(Session session, string outcome, string expected)
    {
        var result = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy ()"), TestContext.CancellationToken);
        Assert.AreEqual(outcome, result.Outcome, result.Original.Detail + "; " + result.Edited.Detail);
        foreach (var (side, value) in new[] { (result.Original, "42"), (result.Edited, expected) })
        {
            Assert.AreEqual("completed", side.Outcome, side.Detail);
            Assert.IsNull(side.Exception);
            Assert.AreEqual(value, side.Result!.Value);
            Assert.HasCount(1, side.Invocations);
        }
    }

    private static void AssertExport(byte[] image)
    {
        var context = new AssemblyLoadContext("sibling-copy", isCollectible: true);
        try
        {
            var assembly = context.LoadFromStream(new MemoryStream(image));
            Assert.AreEqual(43, assembly.GetType("IlRepl.Cell")!.GetMethod("Run")!.Invoke(null, null));
        }
        finally
        {
            context.Unload();
        }
    }
}
