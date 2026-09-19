using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using CecilInstruction = Mono.Cecil.Cil.Instruction;
using CecilMethodAttributes = Mono.Cecil.MethodAttributes;

namespace IlRepl.Engine;

/// <summary>
/// Translates assembly and module lookups only when their receiver is the emitted copy.
/// </summary>
internal sealed partial class ImportedMethodFamily
{
    private MethodDefinition WriteScopedTypeLookup(
        CecilWriter writer,
        TypeDefinition owner,
        MethodReference target,
        OpCode call,
        int index)
    {
        var wrapper = new MethodDefinition(target.Name + index, CecilMethodAttributes.Assembly | CecilMethodAttributes.Static,
            target.ReturnType);
        owner.Methods.Add(wrapper);
        wrapper.Parameters.Add(new ParameterDefinition(target.DeclaringType));
        foreach (var parameter in target.Parameters)
        {
            wrapper.Parameters.Add(new ParameterDefinition(parameter.ParameterType));
        }

        var il = wrapper.Body.GetILProcessor();
        var unmodified = il.Create(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldtoken, owner);
        il.Emit(OpCodes.Call, writer.Import(typeof(Type).GetMethod(nameof(Type.GetTypeFromHandle))!));
        var property = target.DeclaringType.FullName == typeof(Assembly).FullName ? nameof(Type.Assembly) : nameof(Type.Module);
        il.Emit(OpCodes.Callvirt, writer.Import(typeof(Type).GetProperty(property)!.GetMethod!));
        il.Emit(OpCodes.Bne_Un, unmodified);
        il.Emit(OpCodes.Ldarg_1);
        var ignoreCase = target.Name == nameof(Assembly.CreateInstance) ? 1 : 2;
        if (target.Parameters.Count > ignoreCase)
        {
            il.Emit(OpCodes.Ldarg, wrapper.Parameters[ignoreCase + 1]);
        }
        else
        {
            il.Emit(OpCodes.Ldc_I4_0);
        }

        WriteTypeLookupNames(writer, il);
        il.Emit(OpCodes.Call, writer.Import(typeof(CopiedTypeNames).GetMethod(nameof(CopiedTypeNames.TranslateScoped),
            BindingFlags.Static | BindingFlags.NonPublic)!));
        il.Emit(OpCodes.Starg, wrapper.Parameters[1]);
        il.Append(unmodified);
        foreach (var parameter in wrapper.Parameters.Skip(1))
        {
            il.Emit(OpCodes.Ldarg, parameter);
        }

        il.Emit(call, target);
        il.Emit(OpCodes.Ret);
        return wrapper;
    }

    private static void LoadConstrainedLookupReceiver(MethodDefinition method, CecilInstruction call, MethodReference target)
    {
        var prefixes = new List<CecilInstruction>();
        for (var prefix = call.Previous; prefix is not null && prefix.OpCode.OpCodeType == OpCodeType.Prefix; prefix = prefix.Previous)
        {
            prefixes.Add(prefix);
        }

        if (!prefixes.Any(prefix => prefix.OpCode.Code == Code.Constrained))
        {
            return;
        }

        prefixes.Reverse();
        var tail = prefixes.Any(prefix => prefix.OpCode.Code == Code.Tail);
        foreach (var prefix in prefixes)
        {
            prefix.OpCode = OpCodes.Nop;
            prefix.Operand = null;
        }

        var il = method.Body.GetILProcessor();
        var instructions = new List<CecilInstruction>();
        var arguments = target.Parameters.Select(parameter => new VariableDefinition(parameter.ParameterType)).ToArray();
        foreach (var argument in arguments)
        {
            method.Body.Variables.Add(argument);
        }

        foreach (var argument in arguments.Reverse())
        {
            instructions.Add(il.Create(OpCodes.Stloc, argument));
        }
        // Assembly and Module receivers are reference types, including constrained generic instantiations.
        instructions.Add(il.Create(OpCodes.Ldind_Ref));
        foreach (var argument in arguments)
        {
            instructions.Add(il.Create(OpCodes.Ldloc, argument));
        }

        var first = prefixes[0];
        first.OpCode = instructions[0].OpCode;
        first.Operand = instructions[0].Operand;
        foreach (var instruction in instructions.Skip(1))
        {
            il.InsertAfter(first, instruction);
            first = instruction;
        }

        if (tail)
        {
            prefixes[^1].OpCode = OpCodes.Tail;
        }
    }
}
