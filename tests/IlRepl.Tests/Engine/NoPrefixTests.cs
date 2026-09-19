using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
using IlRepl.Engine;
using IlRepl.Engine.Binding;
using IlRepl.Protocol;
using Mono.Cecil;

namespace IlRepl.Tests.Engine;

/// <summary>
/// The no. prefix retains its encoded mask through editable bodies and reports both CIL and runtime restrictions.
/// </summary>
[TestClass]
public sealed class NoPrefixTests
{
    /// <summary>
    /// The cancellation token for actual symbolic editor analysis.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Every legal mask survives assembly and export while imported runtime refusals retain an editable recovery draft.
    /// </summary>
    /// <param name="mask">The type, range, and null checks selected by the prefix.</param>
    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    [DataRow(5)]
    [DataRow(6)]
    [DataRow(7)]
    public void Body_PreservesEncodedMasksAndActualRuntimeRestriction(int mask)
    {
        var assemblyName = "NoPrefixOracle" + Guid.NewGuid().ToString("N");
        var independent = IlasmLocator.Assemble($$"""
            .assembly extern System.Runtime {}
            .assembly {{assemblyName}} {}
            .module {{assemblyName}}.dll
            .class public Fixture extends [System.Runtime]System.Object {
                .method public static int32 Read(int32[] values) cil managed {
                    .maxstack 8
                    ldarg.0
                    ldc.i4.0
                    .emitbyte 0xfe
                    .emitbyte 0x19
                    .emitbyte {{mask}}
                    ldelema int32
                    ldind.i4
                    ret
                }
            }
            """);
        var session = new Session();
        var oracleAssembly = session.Resolver.LoadImage(independent);
        var original = oracleAssembly.GetType("Fixture")!.GetMethod("Read")!;
        AssertRuntimeRestriction(original);
        var disassembly = MethodDisassembler.Disassemble(original, session);
        Assert.IsEmpty(disassembly.Problems);
        var decoded = disassembly.Entries.Single(entry => entry.Raw?.Op.IsSkipChecksPrefix == true);
        Assert.AreEqual(2, decoded.Offset);
        Assert.IsNotNull(decoded.Instruction);
        Assert.AreEqual("no.", decoded.Instruction.DecodedPrefixName);
        Assert.AreEqual((byte)mask, decoded.Instruction.Operand);
        Assert.IsFalse(decoded.EffectUnknown);
        var diagnostics = StackAnalysis.Diagnostics(disassembly);
        Assert.DoesNotContain(diagnostic => diagnostic.Kind == AnalysisDiagnosticKind.Error, diagnostics);
        Assert.Contains(diagnostic => diagnostic.Code == "FLOW007" && diagnostic.Location.Offset == 2
            && diagnostic.Kind == AnalysisDiagnosticKind.Unverifiable, diagnostics);
        var edit = session.PrepareEdit($"int32 [{assemblyName}]Fixture::Read(int32[])", "Copy");
        Assert.HasCount(1, edit.Problems);
        Assert.Contains("runtime rejected", edit.Problems[0]);
        Assert.Contains("Read(int32[])", edit.Problems[0]);
        var prefix = "no. " + mask.ToString(CultureInfo.InvariantCulture);
        Assert.Contains(prefix, edit.Source);
        var retainedSource = edit.Source;
        var rejection = Assert.ThrowsExactly<ReplException>(() => session.CommitEdit(edit.Name, edit.Source));
        Assert.Contains("runtime rejected", rejection.Message);
        Assert.AreEqual(retainedSource, edit.Source);
        Assert.IsNull(edit.Method);
        Assert.AreEqual(0, edit.Revision);
        Assert.AreSame(edit, session.PrepareEdit(edit.Name));
        Assert.ThrowsExactly<ReplException>(() => _ = edit.OriginalMethod);
        session.CommitEdit(edit.Name, edit.Source.Replace(prefix, "nop", StringComparison.Ordinal));
        int[] values = [42];
        Assert.AreEqual(42, edit.Method!.Invoke(null, [values]));
        // An open generic body can be exported before CoreCLR prepares a concrete instantiation.
        var authored = IlLines.Load(".class public GenericPrefix {", ".method public static int32 Read<T>(int32[] values) {",
            ".locals ()", ".maxstack 8", "ldarg.0", "ldc.i4.0", prefix, "ldelema int32", "ldind.i4", "ret", "}", "}");
        using var oracle = ModuleDefinition.ReadModule(new MemoryStream(independent));
        var expected = oracle.Types.Single(type => type.Name == "Fixture").Methods.Single();
        foreach (var image in new[]
        {
            AssemblyExporter.Write(authored, "no-prefix-export"),
            IlasmLocator.Assemble(IlAsmRenderer.Render(authored)),
        })
        {
            using var module = ModuleDefinition.ReadModule(new MemoryStream(image));
            var methods = module.Types.SelectMany(type => type.Methods).Where(method => method.Name == "Read").ToArray();
            Assert.IsNotEmpty(methods);
            foreach (var method in methods)
            {
                CecilOracle.AssertSameMeaning(expected, method, oracle.Assembly.Name.FullName, module.Assembly.Name.FullName);
            }

            var context = new AssemblyLoadContext("no-prefix-" + Guid.NewGuid(), isCollectible: true);
            try
            {
                var loaded = context.LoadFromStream(new MemoryStream(image));
                foreach (var method in loaded.GetTypes().SelectMany(type => type.GetMethods(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)).Where(method => method.Name == "Read"))
                {
                    AssertRuntimeRestriction(method);
                    var bytes = method.GetMethodBody()!.GetILAsByteArray()!;
                    Assert.AreEqual(0xfe, bytes[2]);
                    Assert.AreEqual(0x19, bytes[3]);
                    Assert.AreEqual(mask, bytes[4]);
                }
            }
            finally
            {
                context.Unload();
            }
        }
    }

    /// <summary>
    /// Malformed masks are rejected before entering the live cell.
    /// </summary>
    /// <param name="operand">The unsupported mask spelling or value.</param>
    [TestMethod]
    [DataRow("0")]
    [DataRow("8")]
    [DataRow("255")]
    [DataRow("-1")]
    [DataRow("typecheck")]
    [DataRow("")]
    public void Parse_InvalidMasksAreRejectedAtomically(string operand)
    {
        var session = new Session();

        var failure = Assert.ThrowsExactly<ReplException>(() => session.AddLine("no. " + operand));

        Assert.Contains("no.", failure.Message);
        Assert.IsTrue(session.Cell.IsEmpty);
        Assert.IsEmpty(session.Cell.Entries);
    }

    /// <summary>
    /// Prefix applicability is checked against the selected mask and the actual following operation.
    /// </summary>
    /// <param name="mask">The requested checks.</param>
    /// <param name="operation">The instruction that cannot suppress those checks.</param>
    [TestMethod]
    [DataRow(1, "ldelem.i4")]
    [DataRow(2, "castclass object")]
    [DataRow(4, "nop")]
    public void Body_InvalidFollowingInstructionIsRejected(int mask, string operation)
    {
        var session = IlLines.Load(".method void Invalid() {", "no. " + mask.ToString(CultureInfo.InvariantCulture));

        var failure = Assert.ThrowsExactly<ReplException>(() => session.AddLine(operation));

        Assert.Contains("no. cannot prefix " + operation.Split(' ')[0], failure.Message);
        Assert.IsEmpty(session.Methods);
    }

    /// <summary>
    /// Preview preserves known stack values for legal masks and locates invalid applications without committing source.
    /// </summary>
    [TestMethod]
    public async Task Preview_UsesTypedPrefixAndSharedApplicabilityValidation()
    {
        var source = ".method int32 Read(int32[] values) {\nldarg.0\nldc.i4.0\nno. 2\nldelem.i4\nret\n}";
        var session = new Session();
        using var editor = new EditingSession(session);

        var valid = await editor.AnalyzeAsync(new AnalysisRequest(source.Split('\n'), 5, 0, 1), TestContext.CancellationToken);
        var invalid = await editor.AnalyzeAsync(new AnalysisRequest(source.Replace("no. 2", "no. 1",
            StringComparison.Ordinal).Split('\n'), 5, 0, 2), TestContext.CancellationToken);

        Assert.DoesNotContain(diagnostic => diagnostic.Kind == AnalysisDiagnosticKind.Error, valid.Diagnostics);
        Assert.Contains(diagnostic => diagnostic.Code == "FLOW007" && diagnostic.Location.Line == 3, valid.Diagnostics);
        Assert.AreEqual("[int32]", valid.Stack!.Render());
        Assert.Contains(diagnostic => diagnostic.Code == "FLOW019" && diagnostic.Location.Line == 3, invalid.Diagnostics);
        Assert.IsEmpty(session.Methods);
    }

    private static void AssertRuntimeRestriction(MethodBase method)
    {
        if (method is MethodInfo { IsGenericMethodDefinition: true } generic)
        {
            method = generic.MakeGenericMethod(typeof(int));
        }

        int[] values = [42];
        var failure = Assert.ThrowsExactly<TargetInvocationException>(() => method.Invoke(null, [values]));
        Assert.IsInstanceOfType<InvalidProgramException>(failure.InnerException);
    }
}
