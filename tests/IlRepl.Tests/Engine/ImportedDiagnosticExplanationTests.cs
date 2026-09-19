using IlRepl.Engine;
using IlRepl.Protocol;
using IlRepl.Repl;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Checks diagnostic evidence obtained by decoding real assemblies rather than replaying source.
/// </summary>
[TestClass]
public sealed class ImportedDiagnosticExplanationTests
{
    /// <summary>
    /// A disassembled rejection identifies the producer by its real IL offset and preserves its original instruction text.
    /// </summary>
    [TestMethod]
    public void Disassembly_UsesImportedOffsetsForValueProducers()
    {
        var session = new Session();
        var (_, _, fixture) = CecilFixture.Build((module, type) =>
        {
            var method = new Mono.Cecil.MethodDefinition("WrongArgument", MethodAttributes.Public | MethodAttributes.Static,
                module.TypeSystem.Int32);
            type.Methods.Add(method);
            var il = method.Body.GetILProcessor();
            il.Emit(OpCodes.Ldstr, "wrong");
            il.Emit(OpCodes.Call, module.ImportReference(typeof(Math).GetMethod(nameof(Math.Abs), [typeof(int)])!));
            il.Emit(OpCodes.Ret);
        }, session.Resolver);

        var body = MethodDisassembler.Disassemble(fixture.GetMethod("WrongArgument")!, session);
        StackAnalysis.Run(body, out var diagnostics);

        var diagnostic = Assert.ContainsSingle(diagnostics.Where(item => item.Code == "FLOW005"));
        Assert.AreEqual(5, diagnostic.Location.Offset);
        var facts = diagnostic.Explanation;
        Assert.IsNotNull(facts);
        Assert.IsNotNull(facts.Stack);
        Assert.AreSequenceEqual(["string"], facts.Stack.Values);
        var conflict = Assert.ContainsSingle(facts.Conflicts);
        Assert.AreEqual(0, conflict.Index);
        Assert.AreEqual("argument 1", conflict.Role);
        Assert.AreEqual("int32", conflict.Expected);
        Assert.AreEqual("string", conflict.Actual);
        var producer = Assert.ContainsSingle(conflict.Producers);
        Assert.AreEqual(AnalysisSourceKind.Imported, producer.Kind);
        Assert.AreEqual(0, producer.Location.Offset);
        Assert.AreEqual("WrongArgument", producer.Location.Body);
        Assert.Contains("ldstr \"wrong\"", producer.Source);
        var displayed = string.Join('\n', DiagnosticFormatter.Details(diagnostic));
        Assert.Contains("IL_0000", displayed);
        Assert.DoesNotContain("line 1", displayed);
        using var core = new ReplCore(session, new ReplOptions());
        var listing = core.Handle($".dis [{fixture.Assembly.GetName().Name}]{fixture.FullName}::WrongArgument()");
        Assert.IsTrue(listing.Succeeded, string.Join('\n', core.Transcript.Lines.Select(line => line.PlainText)));
        var transcript = string.Join('\n', core.Transcript.Lines.Select(line => line.PlainText));
        Assert.Contains("Expected:", transcript);
        Assert.Contains("Stack before (bottom → top): [string]", transcript);
        Assert.Contains("argument 1: expected int32; actual string", transcript);
        Assert.Contains("IL_0000: ldstr \"wrong\"", transcript);
    }
}
