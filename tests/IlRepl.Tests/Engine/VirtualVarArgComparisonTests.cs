using IlRepl.Engine;
using IlRepl.Host;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Virtual managed-vararg calls preserve dispatch and optional argument signatures in emitted comparisons.
/// </summary>
[TestClass]
public sealed class VirtualVarArgComparisonTests
{
    /// <summary>
    /// Supplies cancellation for real comparison processes on supported runtimes.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Dispatch pointers use method definitions while actual calls retain optional arguments and execute on Windows.
    /// </summary>
    /// <param name="constrained">Whether the caller uses a constrained receiver.</param>
    /// <param name="behavior">Whether the derived receiver overrides, calls the base, or inherits the selected method.</param>
    [TestMethod]
    [DataRow(false, "override")]
    [DataRow(true, "override")]
    [DataRow(false, "base")]
    [DataRow(true, "base")]
    [DataRow(false, "inherit")]
    [DataRow(true, "inherit")]
    public async Task Compare_VirtualVarargsPreserveDispatchAndOptionalArguments(bool constrained, string behavior)
    {
        var writer = new CecilWriter("virtual-vararg");
        var owner = new TypeDefinition("N", "Owner", TypeAttributes.Public, writer.Object);
        writer.Module.Types.Add(owner);
        Define(writer.Module, owner);
        var selected = owner.Methods.Single(method => method.Name == "Read");
        var entry = ComparisonInstrumentation.Wrap(writer, selected);
        var caller = new MethodDefinition("Scenario", MethodAttributes.Public | MethodAttributes.Static, writer.Module.TypeSystem.Int32);
        owner.Methods.Add(caller);
        caller.Body.InitLocals = true;
        var receiver = new VariableDefinition(owner);
        caller.Body.Variables.Add(receiver);
        var il = caller.Body.GetILProcessor();
        il.Emit(OpCodes.Newobj, owner.Methods.Single(method => method.IsConstructor));
        il.Emit(OpCodes.Stloc, receiver);
        il.Emit(constrained ? OpCodes.Ldloca : OpCodes.Ldloc, receiver);
        il.Emit(OpCodes.Ldc_I4_7);
        il.Emit(OpCodes.Ldc_I4, 42);
        il.Emit(OpCodes.Ldstr, "optional");
        if (constrained)
        {
            il.Emit(OpCodes.Constrained, owner);
        }

        var reference = new MethodReference(entry.Name, entry.ReturnType, owner)
        {
            HasThis = true,
            CallingConvention = MethodCallingConvention.VarArg,
        };
        reference.Parameters.Add(new ParameterDefinition(writer.Module.TypeSystem.Int32));
        reference.Parameters.Add(new ParameterDefinition(new SentinelType(writer.Module.TypeSystem.Int32)));
        reference.Parameters.Add(new ParameterDefinition(writer.Module.TypeSystem.String));
        il.Emit(OpCodes.Callvirt, reference);
        il.Emit(OpCodes.Ret);
        ComparisonInstrumentation.Complete(writer, selected, entry);
        using var module = ModuleDefinition.ReadModule(new MemoryStream(writer.Write()));
        var adapter = module.GetTypes().SelectMany(type => type.Methods)
            .Single(method => method.Name.EndsWith(constrained ? "_constrained" : "_virtual", StringComparison.Ordinal));
        var pointers = adapter.Body.Instructions.Where(instruction => instruction.OpCode.Code is Code.Ldftn or Code.Ldvirtftn)
            .Select(instruction => (MethodReference)instruction.Operand).ToArray();
        Assert.HasCount(2, pointers);
        foreach (var pointer in pointers)
        {
            Assert.HasCount(1, pointer.Parameters);
            Assert.IsTrue(pointer.Resolve().IsVirtual);
            Assert.AreEqual("Read", pointer.Name);
        }

        var dispatch = (MethodReference)adapter.Body.Instructions.Single(instruction => instruction.OpCode.Code == Code.Callvirt).Operand;
        Assert.AreEqual(MethodCallingConvention.VarArg, dispatch.CallingConvention);
        Assert.AreSequenceEqual(["System.Int32", "System.Int32", "System.String"],
            dispatch.Parameters.Select(parameter => parameter.ParameterType.GetElementType().FullName));
        Assert.IsInstanceOfType<SentinelType>(dispatch.Parameters[1].ParameterType);
        if (OperatingSystem.IsWindows())
        {
            var session = new Session();
            var (assembly, _, _) = CecilFixture.Build(Define, session.Resolver);
            var edit = session.PrepareEdit("instance vararg int32 [" + assembly.GetName().Name + "]N.Fixture::Read(int32)", "Copy");
            session.CommitEdit(edit.Name, edit.Source.Replace("ret", "ldc.i4.1\nadd\nret", StringComparison.Ordinal));
            var copiedOwner = TypeNameFormatter.IlAsmDeclaring(edit.Method!.DeclaringType!);
            foreach (var line in Scenario(constrained, behavior, copiedOwner).Split('\n'))
            {
                session.AddLine(line);
            }

            var result = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy using Scenario"),
                TestContext.CancellationToken);
            var invoked = behavior != "override";
            Assert.AreEqual(invoked ? "different" : "incomplete", result.Outcome, result.Original.Detail + "; " + result.Edited.Detail);
            foreach (var (side, expected) in new[] { (result.Original, 9), (result.Edited, 10) })
            {
                Assert.IsNull(side.Exception, side.Exception?.Message);
                Assert.AreEqual((invoked ? expected + (behavior == "base" ? 100 : 0) : 109).ToString(), side.Result!.Value);
                Assert.HasCount(invoked ? 1 : 0, side.Invocations);
                if (invoked)
                {
                    var arguments = side.Invocations[0].Inputs
                        .Where(member => member.Name.StartsWith("argument ", StringComparison.Ordinal));
                    Assert.AreSequenceEqual(["7", "42", "optional"], arguments.Select(member => member.Value.Value));
                }
            }
        }
    }

    private static void Define(ModuleDefinition module, TypeDefinition owner)
    {
        VarArgEditAliasTests.DefineCounter(module, owner);
        var read = owner.Methods.Single();
        read.IsStatic = false;
        read.HasThis = true;
        read.IsVirtual = true;
        read.IsNewSlot = true;
        read.Body.Instructions.Single(instruction => instruction.OpCode.Code == Code.Ldarg_0).OpCode = OpCodes.Ldarg_1;
        var constructor = new MethodDefinition(".ctor", MethodAttributes.Public | MethodAttributes.SpecialName
            | MethodAttributes.RTSpecialName | MethodAttributes.HideBySig, module.TypeSystem.Void) { HasThis = true };
        owner.Methods.Add(constructor);
        var il = constructor.Body.GetILProcessor();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, module.ImportReference(typeof(object).GetConstructor(Type.EmptyTypes)!));
        il.Emit(OpCodes.Ret);
    }

    private static string Scenario(bool constrained, string behavior, string owner)
    {
        var call = "instance vararg int32 " + owner + "::Read(int32, ..., int32, string)";
        var body = behavior == "inherit" ? "" : ".method public virtual instance vararg int32 Read(int32 first) {\n"
            + (behavior == "base" ? "ldarg.0\nldarg.1\nldc.i4.s 42\nldstr \"optional\"\ncall " + call : """
                .locals init (valuetype ArgIterator args)
                arglist
                newobj instance void ArgIterator::.ctor(valuetype RuntimeArgumentHandle)
                stloc.0
                ldloca.s 0
                call instance int32 ArgIterator::GetRemainingCount()
                ldarg.1
                add
                """) + "\nldc.i4.s 100\nadd\nret\n}\n";
        return ".class public Derived extends " + owner + " {\n.method public instance void .ctor() {\nldarg.0\n"
            + "call instance void " + owner + "::.ctor()\nret\n}\n" + body + "}\n"
            + ".method int32 Scenario() {\n.locals init (class Derived receiver)\nnewobj instance void Derived::.ctor()\nstloc.0\n"
            + (constrained ? "ldloca.s 0" : "ldloc.0") + "\nldc.i4.7\nldc.i4.s 42\nldstr \"optional\"\n"
            + (constrained ? "constrained. Derived\n" : "") + "callvirt " + call + "\nret\n}";
    }
}
