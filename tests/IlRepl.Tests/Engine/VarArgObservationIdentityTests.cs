using System.Globalization;
using IlRepl.Engine;
using IlRepl.Host;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Generated vararg entries preserve real helpers whose names resemble observation methods.
/// </summary>
[TestClass]
public sealed class VarArgObservationIdentityTests
{
    /// <summary>
    /// Supplies cancellation for supported real vararg comparison processes.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Entry and optional-wrapper name collisions cannot redirect calls to genuine methods on the original owner.
    /// </summary>
    /// <param name="optionalCount">The optional arguments supplied by the scenario.</param>
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public async Task Compare_VarargHelpersKeepTheirMethodIdentity(int optionalCount)
    {
        var writer = new CecilWriter("vararg-collisions");
        var owner = new TypeDefinition("N", "Fixture", TypeAttributes.Public, writer.Object);
        writer.Module.Types.Add(owner);
        Define(writer.Module, owner);
        var target = owner.Methods.Single(method => method.Name == "Read");
        var entry = ComparisonInstrumentation.Wrap(writer, target);
        var scenario = new MethodDefinition("Scenario", MethodAttributes.Public | MethodAttributes.Static, writer.Module.TypeSystem.Int32);
        owner.Methods.Add(scenario);
        var reference = new MethodReference(entry.Name, entry.ReturnType, owner) { CallingConvention = MethodCallingConvention.VarArg };
        reference.Parameters.Add(new ParameterDefinition(writer.Module.TypeSystem.Int32));
        var il = scenario.Body.GetILProcessor();
        il.Emit(OpCodes.Ldc_I4, 41);
        for (var index = 0; index < optionalCount; index++)
        {
            reference.Parameters.Add(new ParameterDefinition(index == 0
                ? new SentinelType(writer.Module.TypeSystem.Int32) : writer.Module.TypeSystem.Int32));
            il.Emit(OpCodes.Ldc_I4_7);
        }

        il.Emit(OpCodes.Call, reference);
        il.Emit(OpCodes.Ret);
        ComparisonInstrumentation.Complete(writer, target, entry);
        using var exported = ModuleDefinition.ReadModule(new MemoryStream(writer.Write()));
        var exportedOwner = exported.Types.Single(type => type.FullName == "N.Fixture");
        var read = exportedOwner.Methods.Single(method => method.Name == "Read");
        var helpers = read.Body.Instructions.Select(instruction => instruction.Operand).OfType<MethodReference>()
            .Where(method => method.Name.StartsWith("__ilrepl_observe_", StringComparison.Ordinal)).ToArray();
        Assert.AreSequenceEqual(["__ilrepl_observe_Read", "__ilrepl_observe_Read__0"], helpers.Select(method => method.Name));
        foreach (var helper in helpers)
        {
            Assert.AreSame(exportedOwner, helper.Resolve().DeclaringType);
            Assert.IsEmpty(helper.Parameters);
        }

        var observed = exportedOwner.Methods.Single(method => method.Name == "Scenario").Body.Instructions
            .Select(instruction => instruction.Operand).OfType<MethodReference>().Single().Resolve();
        Assert.AreNotSame(exportedOwner, observed.DeclaringType);
        Assert.HasCount(1 + optionalCount, observed.Parameters);
        if (OperatingSystem.IsWindows())
        {
            var session = new Session();
            var (assembly, _, _) = CecilFixture.Build(Define, session.Resolver);
            var edit = session.PrepareEdit("vararg int32 [" + assembly.GetName().Name + "]N.Fixture::Read(int32)", "Copy");
            session.CommitEdit(edit.Name, edit.Source.Replace("ret", "ldc.i4.1\nadd\nret", StringComparison.Ordinal));
            var arguments = string.Join(", ", Enumerable.Repeat("int32", optionalCount));
            var body = ".method int32 Scenario() {\nldc.i4.s 41\n" + string.Concat(Enumerable.Repeat("ldc.i4.7\n", optionalCount))
                + "call vararg int32 Copy(int32" + (optionalCount == 0 ? "" : ", ..., " + arguments) + ")\nret\n}";
            foreach (var line in body.Split('\n'))
            {
                session.AddLine(line);
            }

            var result = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy using Scenario"),
                TestContext.CancellationToken);
            Assert.AreEqual("different", result.Outcome, result.Original.Detail + "; " + result.Edited.Detail);
            Assert.AreEqual((41 + optionalCount).ToString(CultureInfo.InvariantCulture), result.Original.Result!.Value);
            Assert.AreEqual((42 + optionalCount).ToString(CultureInfo.InvariantCulture), result.Edited.Result!.Value);
        }
    }

    private static void Define(ModuleDefinition module, TypeDefinition owner)
    {
        VarArgEditAliasTests.DefineCounter(module, owner);
        var target = owner.Methods.Single();
        var il = target.Body.GetILProcessor();
        var first = target.Body.Instructions[0];
        foreach (var name in new[] { "__ilrepl_observe_Read", "__ilrepl_observe_Read__0" })
        {
            var helper = new MethodDefinition(name, MethodAttributes.Private | MethodAttributes.Static, module.TypeSystem.Int32);
            owner.Methods.Add(helper);
            helper.Body.GetILProcessor().Emit(OpCodes.Ldc_I4_0);
            helper.Body.GetILProcessor().Emit(OpCodes.Ret);
            il.InsertBefore(first, il.Create(OpCodes.Call, helper));
            il.InsertBefore(first, il.Create(OpCodes.Pop));
        }
    }
}
