using IlRepl.Engine;
using IlRepl.Engine.Binding;
using IlRepl.Protocol;
using IlRepl.Repl;
using IlRepl.Tests.Engine;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Instruction = Mono.Cecil.Cil.Instruction;

namespace IlRepl.Tests.Repl;

/// <summary>
/// Checks that function-pointer operands keep the same signature from completion through execution.
/// </summary>
[TestClass]
public sealed class FunctionPointerCompletionTests
{
    /// <summary>
    /// Supplies cancellation for completion requests.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Completed methods and fields preserve their function-pointer calling conventions during submission.
    /// </summary>
    /// <param name="convention">The convention encoded in the fixture's function-pointer signature.</param>
    /// <param name="useModifier">True to encode the primary convention as a return modifier.</param>
    [TestMethod]
    [DataRow(MethodCallingConvention.C, false)]
    [DataRow(MethodCallingConvention.StdCall, false)]
    [DataRow(MethodCallingConvention.ThisCall, false)]
    [DataRow(MethodCallingConvention.FastCall, false)]
    [DataRow(MethodCallingConvention.Default, false)]
    [DataRow((MethodCallingConvention)9, false)]
    [DataRow(MethodCallingConvention.C, true)]
    [DataRow(MethodCallingConvention.StdCall, true)]
    [DataRow(MethodCallingConvention.ThisCall, true)]
    [DataRow(MethodCallingConvention.FastCall, true)]
    public async Task Complete_FunctionPointerMembers_BindAndRun(MethodCallingConvention convention, bool useModifier)
    {
        var session = new Session();
        var (assembly, _, fixture) = CecilFixture.Build((module, type) =>
        {
            var pointer = new FunctionPointerType { ReturnType = module.TypeSystem.Int32, CallingConvention = convention };
            if (useModifier)
            {
                var marker = convention switch
                {
                    MethodCallingConvention.C => typeof(System.Runtime.CompilerServices.CallConvCdecl),
                    MethodCallingConvention.StdCall => typeof(System.Runtime.CompilerServices.CallConvStdcall),
                    MethodCallingConvention.ThisCall => typeof(System.Runtime.CompilerServices.CallConvThiscall),
                    _ => typeof(System.Runtime.CompilerServices.CallConvFastcall),
                };
                pointer.CallingConvention = (MethodCallingConvention)9;
                pointer.ReturnType = new OptionalModifierType(module.ImportReference(marker), pointer.ReturnType);
            }

            pointer.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int32));
            type.Fields.Add(new FieldDefinition("Native", FieldAttributes.Public | FieldAttributes.Static, pointer));
            var accept = new MethodDefinition("Accept", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Int32);
            accept.Parameters.Add(new ParameterDefinition(pointer));
            accept.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_7));
            accept.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            type.Methods.Add(accept);
            var get = new MethodDefinition("FetchPointer", MethodAttributes.Public | MethodAttributes.Static, pointer);
            get.Body.Instructions.Add(Instruction.Create(OpCodes.Ldsfld, type.Fields[0]));
            get.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            type.Methods.Add(get);
        }, session.State.Resolver);
        using var completer = new OperandCompleter(session);
        var owner = $"[{assembly.GetName().Name}]N.Fixture";
        var methodLine = await Complete(completer, $"call {owner}::Accept");
        var fieldLine = await Complete(completer, $"ldsfld {owner}::Native");
        var returnLine = await Complete(completer, $"call {owner}::FetchPointer");
        using var snapshot = BindingSnapshot.Capture(session.State.Context);
        var scope = new SnapshotBindingScope(snapshot);
        var expected = SymbolBinder.BindFieldReference(CilSyntaxParser.ParseFieldReference(fieldLine[7..]), scope).FieldType;
        Assert.AreEqual(expected, RuntimeSymbolImporter.Import(fixture.GetField("Native")!).FieldType);
        Assert.AreEqual(expected, RuntimeSymbolImporter.Import(fixture.GetMethod("Accept")!).Parameters.Single().Type);
        Assert.AreEqual(expected, RuntimeSymbolImporter.Import(fixture.GetMethod("FetchPointer")!).ReturnType);
        Assert.AreEqual(fixture.GetMethod("Accept"), MemberResolver.ResolveMethod(methodLine[5..], session.State.Context, false).Method);
        Assert.AreEqual(fixture.GetField("Native"), MemberResolver.ResolveField(fieldLine[7..], session.State.Context));

        // Read the null pointer and pass it through without invoking an architecture-specific native convention.
        session.AddLine(fieldLine);
        session.AddLine("pop");
        session.AddLine(returnLine);
        session.AddLine(methodLine);
        Assert.AreEqual(7, session.Run().Value);
    }

    /// <summary>
    /// Calling conventions survive modified reflection types nested inside function pointers and element types.
    /// </summary>
    [TestMethod]
    public void Import_NestedFunctionPointers_AgreesWithMetadata()
    {
        var session = new Session();
        var (assembly, _, fixture) = CecilFixture.Build((module, type) =>
        {
            var inner = new FunctionPointerType { ReturnType = module.TypeSystem.Int32, CallingConvention = MethodCallingConvention.C };
            var outer = new FunctionPointerType { ReturnType = inner, CallingConvention = MethodCallingConvention.StdCall };
            outer.Parameters.Add(new ParameterDefinition(new PointerType(inner)));
            outer.Parameters.Add(new ParameterDefinition(new ByReferenceType(inner)));
            type.Fields.Add(new FieldDefinition("Nested", FieldAttributes.Public | FieldAttributes.Static, new ArrayType(outer)));
        }, session.State.Resolver);
        using var snapshot = BindingSnapshot.Capture(session.State.Context);
        var scope = new SnapshotBindingScope(snapshot);
        var syntax = CilSyntaxParser.ParseFieldReference($"[{assembly.GetName().Name}]N.Fixture::Nested");
        var expected = SymbolBinder.BindFieldReference(syntax, scope).FieldType;
        Assert.AreEqual(expected, RuntimeSymbolImporter.Import(fixture.GetField("Nested")!).FieldType);
    }

    private async Task<string> Complete(OperandCompleter completer, string line)
    {
        var reply = await completer.CompleteAsync(new CompletionRequest([line], 0, line.Length, null, []),
            TestContext.CancellationToken);
        Assert.HasCount(1, reply.Items, line);
        return line[..reply.ReplaceStart] + reply.Items[0].InsertText + line[(reply.ReplaceStart + reply.ReplaceLength)..];
    }
}
