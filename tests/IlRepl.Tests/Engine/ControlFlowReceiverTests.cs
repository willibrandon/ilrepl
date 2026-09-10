using IlRepl.Engine;
using IlRepl.Engine.Binding;
using IlRepl.Protocol;
using IlRepl.Tests.Shared;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Rechecks receiver provenance at earlier instructions when later edges complete the graph.
/// </summary>
[TestClass]
public sealed class ControlFlowReceiverTests
{
    /// <summary>
    /// Supplies cancellation for symbolic analysis.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Readonly stores require the original receiver on every incoming path in preview and live submission.
    /// </summary>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task LaterEdge_RechecksEarlierStore(bool originalReceiver)
    {
        var lines = ControlFlowReceiverExamples.Source(originalReceiver);
        var session = new Session();
        using var editing = new EditingSession(session);
        var preview = await editing.AnalyzeAsync(new AnalysisRequest(lines, 10, 0, 1), TestContext.CancellationToken);
        Assert.AreEqual(originalReceiver, !preview.Diagnostics.Any(diagnostic => diagnostic.Kind == AnalysisDiagnosticKind.Error),
            string.Join("; ", preview.Diagnostics.Select(diagnostic => diagnostic.Message)));
        if (!originalReceiver)
        {
            Assert.Contains(diagnostic => diagnostic.Location.Line == 10
                && diagnostic.Message.Contains("through this", StringComparison.Ordinal),
                preview.Diagnostics);
        }

        for (var line = 0; line < lines.Length; line++)
        {
            if (line == 13 && !originalReceiver)
            {
                var error = Assert.ThrowsExactly<ReplException>(() => session.AddLine(lines[line]));
                Assert.Contains("through this", error.Message);
                Assert.IsNotNull(session.OpenMethod);
                return;
            }

            session.AddLine(lines[line]);
        }

        session.AddLine("ldnull");
        session.AddLine("newobj instance void FlowReceiver::.ctor(class FlowReceiver)");
        session.AddLine("ldfld int32 FlowReceiver::Value");
        Assert.AreEqual(42, session.Run().Value);
    }

    /// <summary>
    /// Decoded constructors use the same readonly receiver rule as live and symbolic source bodies.
    /// </summary>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void Disassembly_ChecksTheReadonlyReceiver(bool originalReceiver)
    {
        var session = new Session();
        var (_, _, fixture) = CecilFixture.Build((module, type) =>
        {
            var field = new FieldDefinition("Value", FieldAttributes.Public | FieldAttributes.InitOnly, module.TypeSystem.Int32);
            type.Fields.Add(field);
            var constructor = new MethodDefinition(".ctor",
                MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName, module.TypeSystem.Void);
            constructor.Parameters.Add(new ParameterDefinition(type));
            type.Methods.Add(constructor);
            var il = constructor.Body.GetILProcessor();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, module.ImportReference(typeof(object).GetConstructor(Type.EmptyTypes)!));
            il.Emit(originalReceiver ? OpCodes.Ldarg_0 : OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Stfld, field);
            il.Emit(OpCodes.Ret);
        }, session.Resolver);
        var listing = MethodDisassembler.Disassemble(fixture.GetConstructors()[0], session);
        var diagnostics = StackAnalysis.Diagnostics(listing);
        Assert.AreEqual(originalReceiver, !diagnostics.Any(diagnostic => diagnostic.Kind == AnalysisDiagnosticKind.Error));
        if (!originalReceiver)
        {
            Assert.Contains(diagnostic => diagnostic.Message.Contains("through this", StringComparison.Ordinal), diagnostics);
        }
    }
}
