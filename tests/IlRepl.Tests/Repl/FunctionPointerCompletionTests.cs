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
    [DataRow(MethodCallingConvention.Unmanaged, false)]
    [DataRow(MethodCallingConvention.C, true)]
    [DataRow(MethodCallingConvention.StdCall, true)]
    [DataRow(MethodCallingConvention.ThisCall, true)]
    [DataRow(MethodCallingConvention.FastCall, true)]
    public Task Complete_FunctionPointerMembers_BindAndRun(MethodCallingConvention convention, bool useModifier) =>
        VerifyMembers(convention, useModifier, false, false, false);

    /// <summary>
    /// Managed function pointers retain receiver flags and vararg conventions on ordinary and generic owners.
    /// </summary>
    /// <param name="hasThis">Whether the pointer has a receiver.</param>
    /// <param name="explicitThis">Whether its receiver is explicit.</param>
    /// <param name="vararg">Whether it uses managed varargs.</param>
    /// <param name="generic">Whether the declaring type is constructed.</param>
    [TestMethod]
    [DataRow(true, false, false, false)]
    [DataRow(true, true, false, false)]
    [DataRow(false, false, true, false)]
    [DataRow(true, false, false, true)]
    [DataRow(true, true, false, true)]
    [DataRow(false, false, true, true)]
    public Task Complete_ManagedFunctionPointers_BindAndRun(bool hasThis, bool explicitThis, bool vararg, bool generic) =>
        VerifyMembers(vararg ? MethodCallingConvention.VarArg : MethodCallingConvention.Default,
            false, hasThis, explicitThis, generic);

    private async Task VerifyMembers(MethodCallingConvention convention, bool useModifier,
        bool hasThis, bool explicitThis, bool generic)
    {
        var session = new Session();
        var (assembly, _, definition) = CecilFixture.Build((module, type) =>
        {
            if (generic)
            {
                type.GenericParameters.Add(new GenericParameter("T", type));
            }

            var pointer = new FunctionPointerType
            {
                ReturnType = generic ? type.GenericParameters[0] : module.TypeSystem.Int32,
                CallingConvention = convention,
                HasThis = hasThis,
                ExplicitThis = explicitThis,
            };
            if (useModifier)
            {
                var marker = convention switch
                {
                    MethodCallingConvention.C => typeof(System.Runtime.CompilerServices.CallConvCdecl),
                    MethodCallingConvention.StdCall => typeof(System.Runtime.CompilerServices.CallConvStdcall),
                    MethodCallingConvention.ThisCall => typeof(System.Runtime.CompilerServices.CallConvThiscall),
                    _ => typeof(System.Runtime.CompilerServices.CallConvFastcall),
                };
                pointer.CallingConvention = MethodCallingConvention.Unmanaged;
                pointer.ReturnType = new OptionalModifierType(module.ImportReference(marker), pointer.ReturnType);
            }

            pointer.Parameters.Add(new ParameterDefinition(generic ? type.GenericParameters[0] : module.TypeSystem.Int32));
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
        }, session.State.Resolver, "FunctionPointers" + Guid.NewGuid().ToString("N") + (generic ? "`1" : ""));
        var fixture = generic ? definition.MakeGenericType(typeof(int)) : definition;
        using var completer = new OperandCompleter(session);
        var owner = $"[{assembly.GetName().Name}]{definition.FullName}" + (generic ? "<int32>" : "");
        var fieldLine = await Complete(completer, $"ldsfld {owner}::Native");
        using var snapshot = BindingSnapshot.Capture(session.State.Context);
        var scope = new SnapshotBindingScope(snapshot);
        var expected = SymbolBinder.BindFieldReference(CilSyntaxParser.ParseFieldReference(fieldLine[7..]), scope).FieldType;
        Assert.AreEqual(expected, RuntimeSymbolImporter.Import(fixture.GetField("Native")!).FieldType);
        Assert.AreEqual(fixture.GetField("Native"), MemberResolver.ResolveField(fieldLine[7..], session.State.Context));

        // Read the null pointer and pass it through without invoking an architecture-specific native convention.
        var body = new List<string> { fieldLine, "pop" };
        var methodLine = await Complete(completer, $"call {owner}::Accept");
        var returnLine = await Complete(completer, $"call {owner}::FetchPointer");
        Assert.AreEqual(expected, RuntimeSymbolImporter.Import(fixture.GetMethod("Accept")!).Parameters.Single().Type);
        Assert.AreEqual(expected, RuntimeSymbolImporter.Import(fixture.GetMethod("FetchPointer")!).ReturnType);
        Assert.AreEqual(fixture.GetMethod("Accept"), MemberResolver.ResolveMethod(methodLine[5..], session.State.Context, false).Method);
        body.Add(returnLine);
        body.Add(methodLine);

        foreach (var line in body)
        {
            session.AddLine(line);
        }

        Assert.AreEqual(7, session.Run().Value);

        session.AddLine(".method int32 Check() {");
        foreach (var line in body)
        {
            session.AddLine(line);
        }

        session.AddLine("ret");
        session.AddLine("}");
        session.AddLine("call Check");
        Assert.AreEqual(7, session.Run().Value);

        session.AddLine("call Check");
        var image = IlasmLocator.Assemble(session.ToIlAsm());
        var context = new System.Runtime.Loader.AssemblyLoadContext("pointer-roundtrip", isCollectible: true);
        context.Resolving += (_, name) => name.Name == assembly.GetName().Name ? assembly : null;
        try
        {
            var exported = context.LoadFromStream(new MemoryStream(image));
            Assert.AreEqual(7, exported.GetType("IlRepl.Cell")!.GetMethod("Run")!.Invoke(null, null));
        }
        finally
        {
            context.Unload();
        }
    }

    /// <summary>
    /// Calling conventions survive modified reflection types nested inside function pointers and element types.
    /// </summary>
    /// <param name="managed">Whether the inner pointer carries managed receiver and vararg flags.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Import_NestedFunctionPointers_AgreesWithMetadata(bool managed)
    {
        var session = new Session();
        var (assembly, _, fixture) = CecilFixture.Build((module, type) =>
        {
            var inner = new FunctionPointerType
            {
                ReturnType = module.TypeSystem.Int32,
                CallingConvention = managed ? MethodCallingConvention.VarArg : MethodCallingConvention.C,
                HasThis = managed,
                ExplicitThis = managed,
            };
            var outer = new FunctionPointerType { ReturnType = inner, CallingConvention = MethodCallingConvention.StdCall };
            outer.Parameters.Add(new ParameterDefinition(new PointerType(inner)));
            outer.Parameters.Add(new ParameterDefinition(new ByReferenceType(inner)));
            type.Fields.Add(new FieldDefinition("Nested", FieldAttributes.Public | FieldAttributes.Static, new ArrayType(outer)));
        }, session.State.Resolver, "NestedPointers" + Guid.NewGuid().ToString("N"));
        using var snapshot = BindingSnapshot.Capture(session.State.Context);
        var scope = new SnapshotBindingScope(snapshot);
        var syntax = CilSyntaxParser.ParseFieldReference($"[{assembly.GetName().Name}]{fixture.FullName}::Nested");
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
