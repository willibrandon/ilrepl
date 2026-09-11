using System.Runtime.Loader;
using IlRepl.Engine;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Instruction type operands retain top-level custom modifiers through live and saved emission.
/// </summary>
[TestClass]
public sealed class InstructionModifierTests
{
    private const string Modifier = "[System.Runtime]System.Runtime.CompilerServices.IsVolatile";

    /// <summary>
    /// Newarr and ldtoken keep required and optional modifiers while their runtime type stays unchanged.
    /// </summary>
    [TestMethod]
    public void TypeOperands_TopLevelModifiers_RunRenderAndExportExactly()
    {
        var session = IlLines.Load(
            "ldc.i4.1",
            $"newarr int32 modreq({Modifier}) modopt({Modifier})",
            "pop",
            $"ldtoken int32 modopt({Modifier})",
            "call Type::GetTypeFromHandle(RuntimeTypeHandle)");

        var il = session.ToIlAsm();
        Assert.Contains($"newarr int32 modreq({Modifier}) modopt({Modifier})", il);
        Assert.Contains($"ldtoken int32 modopt({Modifier})", il);
        var image = AssemblyExporter.Write(session, "instruction-modifiers");
        AssertOperands(image);
        AssertOperands(IlasmLocator.Assemble(il));
        Assert.AreEqual(typeof(int), session.Run().Value);

        var context = new AssemblyLoadContext("instruction-modifiers", isCollectible: true);
        try
        {
            var assembly = context.LoadFromStream(new MemoryStream(image));
            Assert.AreEqual(typeof(int), assembly.GetType("IlRepl.Cell")!.GetMethod("Run")!.Invoke(null, null));
        }
        finally
        {
            context.Unload();
        }
    }

    private static void AssertOperands(byte[] image)
    {
        using var definition = AssemblyDefinition.ReadAssembly(new MemoryStream(image));
        var run = definition.MainModule.GetType("IlRepl.Cell").Methods.Single(method => method.Name == "Run");
        var newarr = (TypeReference)run.Body.Instructions.Single(instruction => instruction.OpCode == OpCodes.Newarr).Operand;
        var token = (TypeReference)run.Body.Instructions.Single(instruction => instruction.OpCode == OpCodes.Ldtoken).Operand;
        var optional = Assert.IsInstanceOfType<OptionalModifierType>(newarr);
        Assert.IsInstanceOfType<RequiredModifierType>(optional.ElementType);
        Assert.IsInstanceOfType<OptionalModifierType>(token);
    }
}
