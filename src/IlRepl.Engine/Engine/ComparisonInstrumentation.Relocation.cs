using Mono.Cecil;
using Mono.Cecil.Cil;
using CilInstruction = Mono.Cecil.Cil.Instruction;

namespace IlRepl.Engine;

/// <summary>
/// Keeps observation helpers outside the selected type's reflected member context.
/// </summary>
internal static partial class ComparisonInstrumentation
{
    private static void Relocate(CecilWriter writer, IReadOnlyList<MethodDefinition> wrappers, MethodDefinition selected)
    {
        writer.GrantAccessTo(writer.Name);
        foreach (var wrapper in wrappers)
        {
            Relocate(writer, wrapper, selected);
        }
    }

    private static void Relocate(CecilWriter writer, MethodDefinition wrapper, MethodDefinition selected)
    {
        var owner = wrapper.DeclaringType;
        var sites = writer.Module.GetTypes().SelectMany(type => type.Methods).Where(method => method.HasBody)
            .SelectMany(method => method.Body.Instructions.Select(instruction => (method, instruction)))
            .Where(site => site.instruction.Operand is MethodReference reference && reference.Name == wrapper.Name
                && reference.DeclaringType.GetElementType().FullName == owner.FullName)
            .Select(site => (site.method, site.instruction, reference: (MethodReference)site.instruction.Operand,
                declaring: ((MethodReference)site.instruction.Operand).DeclaringType)).ToArray();
        var name = "__ilrepl_observation_" + writer.Module.Types.Count;
        while (writer.Module.Types.Any(type => type.Namespace == "IlRepl.Comparison" && type.Name == name))
        {
            name += "_";
        }

        var holder = new TypeDefinition("IlRepl.Comparison", name, TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed,
            writer.Object);
        writer.Module.Types.Add(holder);
        foreach (var parameter in owner.GenericParameters)
        {
            var copy = new GenericParameter(parameter.Name, holder)
            {
                Attributes = parameter.Attributes & ~GenericParameterAttributes.VarianceMask,
            };
            holder.GenericParameters.Add(copy);
            foreach (var constraint in parameter.Constraints)
            {
                copy.Constraints.Add(new GenericParameterConstraint(constraint.ConstraintType));
            }
        }

        var instance = wrapper.HasThis;
        if (instance)
        {
            var receiver = Self(owner);
            wrapper.Parameters.Insert(0, new ParameterDefinition("receiver", ParameterAttributes.None,
                owner.IsValueType ? new ByReferenceType(receiver) : receiver));
            wrapper.IsStatic = true;
            wrapper.HasThis = false;
            wrapper.ExplicitThis = false;
        }

        owner.Methods.Remove(wrapper);
        holder.Methods.Add(wrapper);
        MethodDefinition? checkedCall = null;
        MethodDefinition? constrainedCall = null;
        MethodDefinition? virtualFunction = null;
        foreach (var (method, instruction, reference, declaring) in sites)
        {
            var target = wrapper;
            TypeReference? constraint = null;
            if (instance && instruction.OpCode.Code == Code.Callvirt)
            {
                for (var prefix = instruction.Previous; prefix?.OpCode.OpCodeType == OpCodeType.Prefix; prefix = prefix.Previous)
                {
                    if (prefix.OpCode.Code == Code.Constrained)
                    {
                        constraint = (TypeReference)prefix.Operand;
                        Retarget(method, prefix, prefix.Next);
                        method.Body.GetILProcessor().Remove(prefix);
                        break;
                    }
                }

                target = constraint is null ? checkedCall ??= CheckedCall(writer, wrapper, selected, constrained: false)
                    : constrainedCall ??= CheckedCall(writer, wrapper, selected, constrained: true);
                instruction.OpCode = OpCodes.Call;
            }
            else if (instance && instruction.OpCode.Code == Code.Ldvirtftn)
            {
                if (CanDispatch(selected))
                {
                    target = virtualFunction ??= VirtualFunction(writer, wrapper, selected);
                    instruction.OpCode = OpCodes.Call;
                    instruction.Operand = RelocatedReference(target, declaring, reference);
                    continue;
                }

                var il = method.Body.GetILProcessor();
                var duplicate = il.Create(OpCodes.Dup);
                var discard = il.Create(OpCodes.Pop);
                Retarget(method, instruction, duplicate);
                il.InsertBefore(instruction, duplicate);
                il.InsertBefore(instruction, il.Create(OpCodes.Brtrue, discard));
                il.InsertBefore(instruction, il.Create(OpCodes.Ldnull));
                il.InsertBefore(instruction, il.Create(OpCodes.Throw));
                il.InsertBefore(instruction, discard);
                instruction.OpCode = OpCodes.Ldftn;
            }

            instruction.Operand = RelocatedReference(target, declaring, reference, constraint);
        }
    }

    private static MethodReference RelocatedReference(
        MethodDefinition target,
        TypeReference declaring,
        MethodReference original,
        TypeReference? constraint = null)
    {
        TypeReference owner = target.DeclaringType;
        if (target.DeclaringType.HasGenericParameters)
        {
            var constructed = new GenericInstanceType(owner);
            foreach (var argument in declaring is GenericInstanceType generic
                ? generic.GenericArguments : declaring.GenericParameters.Cast<TypeReference>())
            {
                constructed.GenericArguments.Add(argument);
            }

            owner = constructed;
        }

        var reference = new MethodReference(target.Name, target.ReturnType, owner)
        {
            HasThis = target.HasThis,
            ExplicitThis = target.ExplicitThis,
            CallingConvention = target.CallingConvention,
        };
        foreach (var parameter in target.Parameters)
        {
            reference.Parameters.Add(new ParameterDefinition(parameter.Name, parameter.Attributes, parameter.ParameterType));
        }

        foreach (var parameter in target.GenericParameters)
        {
            reference.GenericParameters.Add(new GenericParameter(parameter.Name, reference));
        }

        if (!target.HasGenericParameters)
        {
            return reference;
        }

        var result = new GenericInstanceMethod(reference);
        var arguments = original is GenericInstanceMethod instance
            ? instance.GenericArguments : original.GenericParameters.Cast<TypeReference>();
        foreach (var argument in arguments.Take(target.GenericParameters.Count - (constraint is null ? 0 : 1)))
        {
            result.GenericArguments.Add(argument);
        }

        if (constraint is not null)
        {
            result.GenericArguments.Add(constraint);
        }

        return result;
    }

    private static void Retarget(MethodDefinition method, CilInstruction previous, CilInstruction next)
    {
        foreach (var instruction in method.Body.Instructions)
        {
            if (instruction.Operand == previous)
            {
                instruction.Operand = next;
            }
            else if (instruction.Operand is CilInstruction[] targets)
            {
                for (var index = 0; index < targets.Length; index++)
                {
                    if (targets[index] == previous)
                    {
                        targets[index] = next;
                    }
                }
            }
        }

        foreach (var handler in method.Body.ExceptionHandlers)
        {
            if (handler.TryStart == previous)
            {
                handler.TryStart = next;
            }

            if (handler.TryEnd == previous)
            {
                handler.TryEnd = next;
            }

            if (handler.HandlerStart == previous)
            {
                handler.HandlerStart = next;
            }

            if (handler.HandlerEnd == previous)
            {
                handler.HandlerEnd = next;
            }

            if (handler.FilterStart == previous)
            {
                handler.FilterStart = next;
            }
        }
    }
}
