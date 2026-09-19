using System.Runtime.CompilerServices;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace IlRepl.Engine;

/// <summary>
/// Preserves virtual-call receiver checks when observation helpers use explicit receiver arguments.
/// </summary>
internal static partial class ComparisonInstrumentation
{
    private static MethodDefinition CheckedCall(CecilWriter writer, MethodDefinition wrapper, MethodDefinition selected, bool constrained)
    {
        var owner = selected.DeclaringType;
        var method = new MethodDefinition(wrapper.Name + (constrained ? "_constrained" : "_virtual"),
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig, wrapper.ReturnType);
        wrapper.DeclaringType.Methods.Add(method);
        foreach (var parameter in wrapper.GenericParameters)
        {
            var copy = new GenericParameter(parameter.Name, method) { Attributes = parameter.Attributes };
            method.GenericParameters.Add(copy);
            foreach (var constraint in parameter.Constraints)
            {
                copy.Constraints.Add(new GenericParameterConstraint(constraint.ConstraintType));
            }
        }

        GenericParameter? receiver = null;
        if (constrained)
        {
            receiver = new GenericParameter("TReceiver", method) { Attributes = (GenericParameterAttributes)0x20 };
            method.GenericParameters.Add(receiver);
        }

        foreach (var parameter in wrapper.Parameters)
        {
            method.Parameters.Add(new ParameterDefinition(parameter.Name, parameter.Attributes,
                parameter.Index == 0 && receiver is not null ? new ByReferenceType(receiver) : parameter.ParameterType));
        }

        var il = method.Body.GetILProcessor();
        il.Emit(OpCodes.Ldarg_0);
        if (receiver is not null)
        {
            if (owner.IsValueType)
            {
                var cast = writer.Import(typeof(Unsafe).GetMethods().Single(candidate => candidate.Name == nameof(Unsafe.As)
                    && candidate.IsGenericMethodDefinition && candidate.GetGenericArguments().Length == 2
                    && candidate.GetParameters() is [{ ParameterType.IsByRef: true }]));
                var closed = new GenericInstanceMethod(cast);
                closed.GenericArguments.Add(receiver);
                closed.GenericArguments.Add(Self(owner));
                il.Emit(OpCodes.Call, closed);
            }
            else
            {
                il.Emit(OpCodes.Ldobj, receiver);
                il.Emit(OpCodes.Box, receiver);
                il.Emit(OpCodes.Castclass, Self(owner));
            }
        }

        if (!owner.IsValueType)
        {
            var ready = il.Create(OpCodes.Nop);
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Brtrue, ready);
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Throw);
            il.Append(ready);
        }

        if (CanDispatch(selected))
        {
            var observed = il.Create(OpCodes.Nop);
            var original = RelocatedReference(selected, Self(owner), method);
            var actualPointer = new VariableDefinition(writer.Module.TypeSystem.IntPtr);
            var selectedPointer = new VariableDefinition(writer.Module.TypeSystem.IntPtr);
            method.Body.Variables.Add(actualPointer);
            method.Body.Variables.Add(selectedPointer);
            // Native integer locals also give Mono's interpreter the correct stack type for pointer comparisons.
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Ldvirtftn, original);
            il.Emit(OpCodes.Stloc, actualPointer);
            il.Emit(OpCodes.Ldftn, original);
            il.Emit(OpCodes.Stloc, selectedPointer);
            il.Emit(OpCodes.Ldloc, actualPointer);
            il.Emit(OpCodes.Ldloc, selectedPointer);
            il.Emit(OpCodes.Beq, observed);
            if (receiver is not null)
            {
                il.Emit(OpCodes.Pop);
                il.Emit(OpCodes.Ldarg_0);
            }

            foreach (var parameter in method.Parameters.Skip(1))
            {
                il.Emit(OpCodes.Ldarg, parameter);
            }

            var destination = RelocatedReference(selected, Self(owner), method);
            foreach (var parameter in wrapper.Parameters.Skip(selected.Parameters.Count + 1))
            {
                destination.Parameters.Add(new ParameterDefinition(destination.Parameters.Count == selected.Parameters.Count
                    ? new SentinelType(parameter.ParameterType) : parameter.ParameterType));
            }

            if (receiver is not null)
            {
                il.Emit(OpCodes.Constrained, receiver);
            }

            il.Emit(OpCodes.Callvirt, destination);
            il.Emit(OpCodes.Ret);
            il.Append(observed);
        }

        foreach (var parameter in method.Parameters.Skip(1))
        {
            il.Emit(OpCodes.Ldarg, parameter);
        }

        var called = new MethodReference(wrapper.Name, wrapper.ReturnType, Self(wrapper.DeclaringType))
        {
            CallingConvention = wrapper.CallingConvention,
        };
        foreach (var parameter in wrapper.Parameters)
        {
            called.Parameters.Add(new ParameterDefinition(parameter.ParameterType));
        }

        foreach (var parameter in wrapper.GenericParameters)
        {
            called.GenericParameters.Add(new GenericParameter(parameter.Name, called));
        }

        if (wrapper.HasGenericParameters)
        {
            var closed = new GenericInstanceMethod(called);
            foreach (var argument in method.GenericParameters.Take(wrapper.GenericParameters.Count))
            {
                closed.GenericArguments.Add(argument);
            }

            called = closed;
        }

        il.Emit(OpCodes.Call, called);
        il.Emit(OpCodes.Ret);
        return method;
    }
}
