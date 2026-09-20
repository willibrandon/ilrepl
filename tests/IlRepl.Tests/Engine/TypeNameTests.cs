using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Protocol;
using IlRepl.Tests.Shared;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Actual type-name identity changes reject recoverably while unchanged names preserve execution, workers and exports.
/// </summary>
[TestClass]
public sealed class TypeNameTests
{
    /// <summary>
    /// Supplies cancellation for real comparison processes.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Enumerates copied type-name identities and their direct or indirect inspection routes.
    /// </summary>
    public static IEnumerable<(string Shape, string Api, string Dispatch)> Cases => TypeNameFixture.Cases;

    /// <summary>
    /// Enumerates provably unchanged names, ordinary members, null values and null receivers.
    /// </summary>
    public static IEnumerable<(string Shape, string Api, string Dispatch)> SupportedCases => TypeNameFixture.SupportedCases;

    /// <summary>
    /// Independently observed source names cannot silently change in an unchanged copy or overwrite a corrected revision.
    /// </summary>
    /// <param name="shape">The copied or unproven queried type shape.</param>
    /// <param name="api">The reflected name API.</param>
    /// <param name="dispatch">The actual getter, reflection, delegate or pointer route.</param>
    [TestMethod]
    [DynamicData(nameof(Cases))]
    public async Task Edit_ChangedTypeNamesRejectAndRecover(string shape, string api, string dispatch)
    {
        var fixture = TypeNameFixture.Create(shape, api, dispatch);
        var session = new Session();
        var assembly = session.Resolver.LoadImage(fixture.Image);
        var original = AssertSource(fixture.Image, assembly, fixture.Expected, shape);
        var edit = session.PrepareEdit("int32 [" + fixture.AssemblyName + "]TypeNames.Owner::Read()", "Copy");
        AssertProblem(edit, api);
        var initial = edit.Source;
        var completion = session.CompletionRevision;
        var failure = Assert.ThrowsExactly<ReplException>(() => session.CommitEdit(edit.Name, initial));
        Assert.Contains(TypeNameFixture.Problem, failure.Message);
        Assert.IsNull(edit.Method);
        Assert.AreEqual(0, edit.Revision);
        Assert.AreEqual(initial, edit.Source);
        Assert.AreEqual(completion, session.CompletionRevision);
        Assert.AreEqual(42, original.Invoke(null, null));

        session.CommitEdit(edit.Name, ".method public static int32 Read() {\nldc.i4.s 43\nret\n}");
        Assert.IsEmpty(edit.Problems);
        Assert.AreEqual(43, edit.Method!.Invoke(null, null));
        var corrected = edit.Source;
        var previous = edit.Method;
        completion = session.CompletionRevision;
        failure = Assert.ThrowsExactly<ReplException>(() => session.CommitEdit(edit.Name, initial));
        Assert.Contains(TypeNameFixture.Problem, failure.Message);
        Assert.AreSame(previous, edit.Method);
        Assert.AreEqual(corrected, edit.Source);
        Assert.AreEqual(1, edit.Revision);
        Assert.AreEqual(completion, session.CompletionRevision);
        Assert.AreEqual(43, edit.Method!.Invoke(null, null));
        Assert.AreEqual(42, original.Invoke(null, null));
        AssertSource(fixture.Image, assembly, fixture.Expected, shape);
        await AssertComparison(session, 43);
        AssertExports(session, assembly, 43);
    }

    /// <summary>
    /// Safe type-name queries preserve actual source strings and detect edits in separate runtimes and exported images.
    /// </summary>
    /// <param name="shape">The BCL, retained sibling, unchanged copied shape or null control.</param>
    /// <param name="api">The reflected name API.</param>
    /// <param name="dispatch">The actual getter, reflection, delegate, pointer or metadata-token route.</param>
    [TestMethod]
    [DynamicData(nameof(SupportedCases))]
    public async Task Edit_UnchangedTypeNamesRemainSupported(string shape, string api, string dispatch)
    {
        var fixture = TypeNameFixture.Create(shape, api, dispatch);
        var session = new Session();
        var assembly = session.Resolver.LoadImage(fixture.Image);
        var original = AssertSource(fixture.Image, assembly, fixture.Expected, shape);
        var edit = session.PrepareEdit("int32 [" + fixture.AssemblyName + "]TypeNames.Owner::Read()", "Copy");
        Assert.IsEmpty(edit.Problems, string.Join("; ", edit.Problems));
        session.CommitEdit(edit.Name, edit.Source);
        Assert.AreEqual(42, edit.Method!.Invoke(null, null));
        var copiedOwner = edit.Method.DeclaringType!;
        Assert.AreNotSame(original.DeclaringType, copiedOwner);
        Assert.AreNotEqual(original.DeclaringType!.FullName, copiedOwner.FullName);
        AssertQuery(copiedOwner, fixture.Expected, shape);
        if (shape == "sibling")
        {
            Assert.Contains(dependency => dependency.Symbol == "Sibling" && dependency.Assembly == assembly.FullName
                && dependency.Disposition == "external" && dependency.Access == "public", edit.Dependencies);
            var query = copiedOwner.GetMethod("Query", BindingFlags.NonPublic | BindingFlags.Static)!;
            var retained = Assert.ContainsSingle(MethodDisassembler.Disassemble(query, session).Entries
                .Select(entry => entry.Instruction?.Operand).OfType<Type>().Where(type => type.FullName == "TypeNames.Sibling"));
            Assert.AreSame(assembly.GetType("TypeNames.Sibling"), retained);
        }

        await AssertComparison(session, 42);
        AssertExports(session, assembly, 42);

        var position = edit.Source.LastIndexOf("ret", StringComparison.Ordinal);
        Assert.IsGreaterThan(0, position);
        session.CommitEdit(edit.Name, edit.Source.Insert(position, "ldc.i4.1\nadd\n"));
        Assert.AreEqual(2, edit.Revision);
        Assert.AreEqual(43, edit.Method!.Invoke(null, null));
        Assert.AreEqual(42, edit.OriginalMethod.Invoke(null, null));
        Assert.AreEqual(42, original.Invoke(null, null));
        AssertQuery(edit.Method.DeclaringType!, fixture.Expected, shape);
        await AssertComparison(session, 43);
        AssertExports(session, assembly, 43);
    }

    private static MethodInfo AssertSource(byte[] image, Assembly assembly, string? expected, string shape)
    {
        using var reader = new PEReader(new MemoryStream(image));
        var metadata = reader.GetMetadataReader();
        var definition = metadata.GetAssemblyDefinition();
        Assert.StartsWith("TypeNameSource", metadata.GetString(definition.Name));
        Assert.AreEqual(new Version(7, 8, 9, 10), definition.Version);
        Assert.AreEqual(metadata.GetString(definition.Name), assembly.GetName().Name);
        Assert.AreEqual(metadata.GetGuid(metadata.GetModuleDefinition().Mvid), assembly.ManifestModule.ModuleVersionId);
        var owner = assembly.GetType("TypeNames.Owner", throwOnError: true)!;
        var sibling = assembly.GetType("TypeNames.Sibling", throwOnError: true)!;
        Assert.AreSame(owner.Assembly, sibling.Assembly);
        Assert.IsTrue(sibling.IsPublic);
        var auxiliary = assembly.GetType("TypeNames.Auxiliary", throwOnError: true)!;
        Assert.IsTrue(auxiliary.IsNotPublic);
        var nested = owner.GetNestedType("Nested", BindingFlags.NonPublic)!;
        Assert.IsTrue(nested.IsNestedPrivate);
        Assert.AreSame(owner, nested.DeclaringType);
        Assert.AreEqual("TypeNames.Owner", owner.FullName);
        Assert.AreEqual("TypeNames.Owner+Nested", nested.FullName);
        AssertQuery(owner, expected, shape);
        var original = owner.GetMethod("Read")!;
        Assert.AreEqual(42, original.Invoke(null, null));
        return original;
    }

    private static void AssertQuery(Type owner, string? expected, string shape)
    {
        var query = owner.GetMethod("Query", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.IsNotNull(query);
        if (shape == "null")
        {
            var error = Assert.ThrowsExactly<TargetInvocationException>(() => query.Invoke(null, null));
            Assert.IsInstanceOfType<NullReferenceException>(error.InnerException);
        }
        else
        {
            Assert.AreEqual(expected, query.Invoke(null, null));
        }
    }

    private static void AssertProblem(MethodEdit edit, string api)
    {
        Assert.Contains(problem => problem.Contains(TypeNameFixture.Problem, StringComparison.Ordinal),
            edit.Problems, string.Join("; ", edit.Problems));
        Assert.Contains(dependency => dependency.Symbol.Contains(api, StringComparison.Ordinal)
            && dependency.Disposition.Contains(TypeNameFixture.Problem, StringComparison.Ordinal), edit.Dependencies);
    }

    private async Task AssertComparison(Session session, int expected)
    {
        var result = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy ()"), TestContext.CancellationToken);
        Assert.AreEqual(expected == 42 ? "match" : "different", result.Outcome, result.Original.Detail + "; " + result.Edited.Detail);
        AssertSide(result.Original, "42");
        AssertSide(result.Edited, expected.ToString());
    }

    private static void AssertSide(ComparisonSide side, string expected)
    {
        Assert.AreEqual("completed", side.Outcome, side.Detail);
        Assert.IsNull(side.Exception);
        Assert.IsNotNull(side.Result);
        Assert.AreEqual("scalar", side.Result.Kind);
        Assert.AreEqual(expected, side.Result.Value);
        var invocation = Assert.ContainsSingle(side.Invocations);
        Assert.IsNull(invocation.Exception);
        Assert.AreEqual("null", invocation.Inputs.Single(member => member.Name == "receiver").Value.Kind);
        var returned = invocation.Outputs.Single(member => member.Name == "return").Value;
        Assert.AreEqual("scalar", returned.Kind);
        Assert.AreEqual(expected, returned.Value);
    }

    private static void AssertExports(Session session, Assembly source, int expected)
    {
        session.AddLine("call Copy");
        var images = new[] { AssemblyExporter.Write(session, "type-name-copy"), IlasmLocator.Assemble(session.ToIlAsm()) };
        session.ClearCell();
        foreach (var image in images)
        {
            var context = new AssemblyLoadContext("type-name-copy", isCollectible: true);
            context.Resolving += (_, name) => name.Name == source.GetName().Name ? source : null;
            try
            {
                var exported = context.LoadImage(image);
                Assert.AreEqual(expected, exported.GetType("IlRepl.Cell")!.GetMethod("Run")!.Invoke(null, null));
            }
            finally
            {
                context.Unload();
            }
        }
    }
}
