using System.Runtime.CompilerServices;
using IlRepl.Engine;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Comparison call sites pass fixed and optional vararg values through typed observation wrappers.
/// </summary>
[TestClass]
public sealed class VarArgComparisonTests
{
    /// <summary>
    /// Exported wrappers retain optional modifiers and sentinels while exposing ordinary callable entry points.
    /// </summary>
    /// <param name="optionalCount">The number of optional arguments supplied by the scenario.</param>
    /// <param name="external">Whether the observation must call an external original whose context cannot be copied.</param>
    [TestMethod]
    [DataRow(0, false)]
    [DataRow(1, false)]
    [DataRow(2, false)]
    [DataRow(0, true)]
    [DataRow(1, true)]
    [DataRow(2, true)]
    public void Export_VarargObservation_ForwardsEveryArgument(int optionalCount, bool external)
    {
        var writer = new CecilWriter("VarArgObservation");
        var owner = new TypeDefinition("N", "VarArgObservation", TypeAttributes.Public, writer.Object);
        writer.Module.Types.Add(owner);
        VarArgEditAliasTests.DefineCounter(writer.Module, owner);
        var target = owner.Methods.Single();
        MethodReference? original = null;
        if (external)
        {
            var (_, _, fixture) = CecilFixture.Build(ExternalVarArgFixture.Define);
            original = writer.Import(fixture.GetMethod("Read")!);
        }

        var entry = ComparisonInstrumentation.Wrap(writer, target, original);
        var scenario = new MethodDefinition("Scenario", MethodAttributes.Public | MethodAttributes.Static, writer.Module.TypeSystem.Int32);
        owner.Methods.Add(scenario);
        var call = new MethodReference(entry.Name, entry.ReturnType, owner) { CallingConvention = MethodCallingConvention.VarArg };
        call.Parameters.Add(new ParameterDefinition(writer.Module.TypeSystem.Int32));
        var il = scenario.Body.GetILProcessor();
        il.Emit(OpCodes.Ldc_I4, 41);
        for (var index = 0; index < optionalCount; index++)
        {
            TypeReference parameter = new OptionalModifierType(writer.Import(typeof(IsLong)),
                writer.Module.TypeSystem.Int32);
            call.Parameters.Add(new ParameterDefinition(index == 0 ? new SentinelType(parameter) : parameter));
            il.Emit(OpCodes.Ldc_I4_7);
        }

        il.Emit(OpCodes.Call, call);
        il.Emit(OpCodes.Ret);
        ComparisonInstrumentation.Complete(writer, target, entry, original);

        using var module = ModuleDefinition.ReadModule(new MemoryStream(writer.Write()));
        var exportedOwner = module.Types.Single(type => type.Name == owner.Name);
        var exportedScenario = exportedOwner.Methods.Single(method => method.Name == "Scenario");
        var observedCall = exportedScenario.Body.Instructions.Select(instruction => instruction.Operand).OfType<MethodReference>().Single();
        var observed = observedCall.Resolve();
        Assert.AreEqual(MethodCallingConvention.Default, observedCall.CallingConvention);
        Assert.HasCount(1 + optionalCount, observed.Parameters);
        var forwarded = observed.Body.Instructions.Select(instruction => instruction.Operand).OfType<MethodReference>()
            .Single(method => method.Name == "Read");
        Assert.AreEqual(MethodCallingConvention.VarArg, forwarded.CallingConvention);
        Assert.AreEqual(original?.DeclaringType.FullName ?? owner.FullName, forwarded.DeclaringType.FullName);
        Assert.AreEqual(original?.DeclaringType.Scope.Name ?? module.Name, forwarded.DeclaringType.Scope.Name);
        Assert.HasCount(1 + optionalCount, forwarded.Parameters);
        for (var index = 0; index < optionalCount; index++)
        {
            var parameter = forwarded.Parameters[index + 1].ParameterType;
            Assert.AreEqual(index == 0, parameter.IsSentinel);
            var annotated = index == 0 ? ((SentinelType)parameter).ElementType : parameter;
            Assert.AreEqual("System.Runtime.CompilerServices.IsLong", ((OptionalModifierType)annotated).ModifierType.FullName);
        }
    }
}
