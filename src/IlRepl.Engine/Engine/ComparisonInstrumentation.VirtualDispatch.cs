using Mono.Cecil;
using Mono.Cecil.Cil;

namespace IlRepl.Engine;

/// <summary>
/// Resolves virtual calls before observing the selected implementation and preserves override declarations.
/// </summary>
internal static partial class ComparisonInstrumentation
{
    private static bool CanDispatch(MethodDefinition method) => method.IsVirtual && !method.IsFinal && !method.DeclaringType.IsValueType;

    private static void RestoreVirtualReferences(CecilWriter writer, MethodDefinition selected, MethodDefinition entry)
    {
        if (!selected.IsVirtual)
        {
            return;
        }

        foreach (var method in writer.Module.GetTypes().SelectMany(type => type.Methods))
        {
            for (var index = 0; index < method.Overrides.Count; index++)
            {
                if (References(method.Overrides[index], entry))
                {
                    var original = method.Overrides[index];
                    method.Overrides[index] = RelocatedReference(selected, original.DeclaringType, original).GetElementMethod();
                }
            }

            if (!method.HasBody || method == entry || method == selected)
            {
                continue;
            }

            foreach (var instruction in method.Body.Instructions)
            {
                if (instruction.Operand is not MethodReference reference)
                {
                    continue;
                }

                if (instruction.OpCode.Code == Code.Ldtoken && References(reference, entry))
                {
                    instruction.Operand = RelocatedReference(selected, reference.DeclaringType, reference);
                }
                else if (instruction.OpCode.Code != Code.Ldtoken && References(reference, selected) && reference.Resolve() == selected)
                {
                    var observed = RelocatedReference(entry, reference.DeclaringType, reference);
                    foreach (var parameter in reference.Parameters.Skip(selected.Parameters.Count))
                    {
                        observed.Parameters.Add(new ParameterDefinition(parameter.ParameterType));
                    }

                    instruction.Operand = observed;
                }
            }
        }
    }

    private static bool References(MethodReference reference, MethodDefinition method) => reference.Name == method.Name
        && reference.DeclaringType.GetElementType().FullName == method.DeclaringType.FullName;

    private static MethodDefinition VirtualFunction(CecilWriter writer, MethodDefinition wrapper, MethodDefinition selected)
    {
        var method = new MethodDefinition(wrapper.Name + "_function",
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig, writer.Module.TypeSystem.IntPtr);
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

        var owner = Self(selected.DeclaringType);
        method.Parameters.Add(new ParameterDefinition("receiver", ParameterAttributes.None, owner));
        var il = method.Body.GetILProcessor();
        var original = RelocatedReference(selected, owner, method);
        var done = il.Create(OpCodes.Ret);
        var actualPointer = new VariableDefinition(writer.Module.TypeSystem.IntPtr);
        var selectedPointer = new VariableDefinition(writer.Module.TypeSystem.IntPtr);
        method.Body.Variables.Add(actualPointer);
        method.Body.Variables.Add(selectedPointer);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldvirtftn, original);
        il.Emit(OpCodes.Stloc, actualPointer);
        il.Emit(OpCodes.Ldftn, original);
        il.Emit(OpCodes.Stloc, selectedPointer);
        il.Emit(OpCodes.Ldloc, actualPointer);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldloc, selectedPointer);
        il.Emit(OpCodes.Bne_Un, done);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldftn, RelocatedReference(wrapper, owner, method));
        il.Emit(OpCodes.Stloc, actualPointer);
        il.Emit(OpCodes.Ldloc, actualPointer);
        il.Append(done);
        return method;
    }
}
