using System.Reflection;
using System.Runtime.Loader;
using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Protocol;
using IlRepl.Tests.Shared;
using Mono.Cecil;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Exact signatures copy source helper context while incompatible nominal identities remain blocked at external boundaries.
/// </summary>
[TestClass]
public sealed class ExactBoundaryTests
{
    /// <summary>
    /// Supplies cancellation for real comparison workers.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Source helpers copy hidden nominal references while retained fields and unrelated external types keep their original boundaries.
    /// </summary>
    /// <param name="kind">The exact parameter, return, or field signature shape.</param>
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
    public async Task Edit_SameAssemblyExactSignaturesCopyOnlyRequiredMethodContext(string kind)
    {
        foreach (var copiedType in new[] { true, false })
        {
            await CheckBoundary(kind, copiedType, separate: false);
        }
    }

    /// <summary>
    /// Two actual assemblies preserve exact external boundary refusals, compatible signatures, atomic recovery and executable exports.
    /// </summary>
    /// <param name="kind">The exact parameter, return, or field signature shape.</param>
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
            await CheckBoundary(kind, copiedType, separate: true);
        }
    }

    private async Task CheckBoundary(string kind, bool copiedType, bool separate)
    {
        var session = new Session();
        var images = separate ? ExactBoundaryFixture.CreateExternal(kind, copiedType)
            : (Source: ExactBoundaryFixture.Create(kind, copiedType), Helper: Array.Empty<byte>());
        var assembly = session.Resolver.LoadImage(images.Source);
        var helper = separate ? session.Resolver.LoadImage(images.Helper) : assembly;
        var owner = assembly.GetType("Owner")!;
        var external = helper.GetType("External")!;
        if (separate)
        {
            Assert.AreNotSame(owner.Assembly, external.Assembly);
            Assert.Contains(reference => reference.Name == helper.GetName().Name, assembly.GetReferencedAssemblies());
            Assert.Contains(reference => reference.Name == assembly.GetName().Name, helper.GetReferencedAssemblies());
        }
        else
        {
            Assert.AreSame(owner.Assembly, external.Assembly);
        }

        var original = owner.GetMethod("Read")!;
        Assert.AreEqual(42, original.Invoke(null, null));
        var edit = session.PrepareEdit("int32 [" + assembly.GetName().Name + "]Owner::Read()", "Copy");
        var initialSource = edit.Source;
        var field = kind.StartsWith("field", StringComparison.Ordinal);
        var blocked = copiedType && (separate || field);
        var copiedHelper = copiedType && !separate && !field;
        if (blocked)
        {
            Assert.HasCount(1, edit.Problems);
            Assert.Contains(field ? "external field" : "external member", edit.Problems[0]);
            Assert.Contains("original nominal type", edit.Problems[0]);
            Assert.DoesNotContain("runtime rejected", edit.Problems[0]);
            var completion = session.CompletionRevision;
            var failure = Assert.ThrowsExactly<ReplException>(() => session.CommitEdit(edit.Name, initialSource));
            Assert.Contains("original nominal type", failure.Message);
            Assert.IsNull(edit.Method);
            Assert.AreEqual(0, edit.Revision);
            Assert.AreEqual(initialSource, edit.Source);
            Assert.AreEqual(completion, session.CompletionRevision);
        }
        else
        {
            Assert.IsEmpty(edit.Problems, string.Join('\n', edit.Problems));
            session.CommitEdit(edit.Name, initialSource);
            Assert.AreEqual(42, edit.Method!.Invoke(null, null));
            if (copiedHelper)
            {
                var target = MethodDisassembler.Disassemble(edit.Method, session).Entries
                    .Select(entry => entry.Instruction?.Operand).OfType<ResolvedMethod>()
                    .Select(method => method.Method!).Single(method => method.Name == "Transfer");
                Assert.AreSame(edit.Method.Module.Assembly, target.Module.Assembly);
                Assert.AreNotSame(helper, target.Module.Assembly);
            }

            await AssertComparison(session, "42");
            AssertExports(session, edit, kind, copiedHelper, assembly, helper, 42);
        }

        var replacement = blocked ? ".method public static int32 Read() {\nldc.i4.s 43\nret\n}"
            : initialSource.Insert(initialSource.LastIndexOf("ret", StringComparison.Ordinal), "ldc.i4.1\nadd\n");
        session.CommitEdit(edit.Name, replacement);
        Assert.IsEmpty(edit.Problems, string.Join('\n', edit.Problems));
        Assert.AreEqual(43, edit.Method!.Invoke(null, null));
        Assert.AreEqual(42, original.Invoke(null, null));
        if (blocked)
        {
            var committed = edit.Method;
            var completion = session.CompletionRevision;
            var failure = Assert.ThrowsExactly<ReplException>(() => session.CommitEdit(edit.Name, initialSource));
            Assert.Contains("original nominal type", failure.Message);
            Assert.AreSame(committed, edit.Method);
            Assert.AreEqual(replacement, edit.Source);
            Assert.AreEqual(1, edit.Revision);
            Assert.AreEqual(completion, session.CompletionRevision);
            Assert.AreEqual(43, edit.Method.Invoke(null, null));
        }

        await AssertComparison(session, "43");
        AssertExports(session, edit, kind, copiedHelper, assembly, helper, 43);
    }

    private async Task AssertComparison(Session session, string edited)
    {
        var result = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy ()"),
            TestContext.CancellationToken);
        Assert.AreEqual(edited == "42" ? "match" : "different", result.Outcome, result.Original.Detail + "; " + result.Edited.Detail);
        AssertSide(result.Original, "42");
        AssertSide(result.Edited, edited);
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

    private static void AssertExports(
        Session session,
        MethodEdit edit,
        string kind,
        bool copiedHelper,
        Assembly source,
        Assembly helper,
        int expected)
    {
        session.AddLine("call Copy");
        var exports = new[] { AssemblyExporter.Write(session, "exact-boundary"), IlasmLocator.Assemble(session.ToIlAsm()) };
        session.ClearCell();
        foreach (var image in exports)
        {
            if (copiedHelper)
            {
                AssertCopiedSignature(image, edit.Method!.DeclaringType!.FullName!, kind);
            }

            var context = new AssemblyLoadContext("exact-boundary", isCollectible: true);
            context.Resolving += (_, name) => name.Name == source.GetName().Name ? source
                : name.Name == helper.GetName().Name ? helper : null;
            try
            {
                var exported = context.LoadImage(image);
                Assert.AreEqual(expected, exported.GetType("IlRepl.Cell")!.GetMethod("Run")!.Invoke(null, null));
                Assert.AreEqual(expected, exported.GetType(edit.Method!.DeclaringType!.FullName!)!.GetMethod("Read")!.Invoke(null, null));
            }
            finally
            {
                context.Unload();
            }
        }
    }

    private static void AssertCopiedSignature(byte[] image, string ownerName, string kind)
    {
        using var moduleStream = new MemoryStream(image);
        using var module = ModuleDefinition.ReadModule(moduleStream);
        var owner = module.Types.Single(type => type.FullName == ownerName);
        var read = owner.Methods.Single(method => method.Name == "Read");
        var helper = read.Body.Instructions.Select(instruction => instruction.Operand).OfType<MethodReference>()
            .Single(method => method.Name == "Transfer");
        Assert.AreSame(module, helper.Resolve().Module);
        AssertNominal(helper, owner, kind);
        AssertNominal(helper.Resolve(), owner, kind);
    }

    private static void AssertNominal(MethodReference helper, TypeDefinition owner, string kind)
    {
        var signature = kind.StartsWith("parameter", StringComparison.Ordinal)
            ? Assert.ContainsSingle(helper.Parameters).ParameterType : helper.ReturnType;
        var nominal = kind switch
        {
            "parameter-modifier" => Assert.IsInstanceOfType<OptionalModifierType>(signature).ModifierType,
            "return-modifier" => Assert.IsInstanceOfType<RequiredModifierType>(signature).ModifierType,
            "parameter-pointer-modifier" => Assert.IsInstanceOfType<OptionalModifierType>(
                Assert.IsInstanceOfType<PointerType>(signature).ElementType).ModifierType,
            "parameter-function-parameter" => Assert.ContainsSingle(
                Assert.IsInstanceOfType<FunctionPointerType>(signature).Parameters).ParameterType,
            "parameter-function-modifier" => Assert.IsInstanceOfType<OptionalModifierType>(
                Assert.IsInstanceOfType<FunctionPointerType>(signature).ReturnType).ModifierType,
            _ => Assert.IsInstanceOfType<FunctionPointerType>(signature).ReturnType,
        };

        Assert.AreEqual(owner.FullName, nominal.FullName);
        Assert.AreSame(owner, nominal.Resolve());
    }
}
