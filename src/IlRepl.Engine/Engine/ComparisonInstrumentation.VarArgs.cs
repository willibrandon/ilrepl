using Mono.Cecil;

namespace IlRepl.Engine;

/// <summary>
/// Supplies typed observation wrappers that forward every optional argument to a managed-vararg method.
/// </summary>
internal static partial class ComparisonInstrumentation
{
    internal static void Complete(CecilWriter writer, MethodDefinition target, MethodDefinition entry,
        MethodReference? externalVarArg = null)
    {
        RestoreVirtualReferences(writer, target, entry);
        if (target.CallingConvention != MethodCallingConvention.VarArg)
        {
            Relocate(writer, [entry], target);
            return;
        }

        var calls = writer.Module.GetTypes().SelectMany(type => type.Methods).Where(method => method.HasBody)
            .SelectMany(method => method.Body.Instructions.Select(instruction => (method, instruction)))
            .Where(site => site.instruction.Operand is MethodReference reference
                && reference.Name == entry.Name && reference.DeclaringType.FullName == entry.DeclaringType.FullName).ToArray();
        var wrappers = new List<MethodDefinition>();
        foreach (var (caller, instruction) in calls)
        {
            var reference = (MethodReference)instruction.Operand;
            var optional = reference.Parameters.Skip(target.Parameters.Count)
                .Select(parameter => parameter.ParameterType is SentinelType sentinel ? sentinel.ElementType : parameter.ParameterType)
                .ToArray();
            if (optional.Length == 0)
            {
                instruction.Operand = entry;
                continue;
            }

            var context = optional.Any(type => type.ContainsGenericParameter) ? caller : null;
            var parameters = context is null ? [] : context.DeclaringType.GenericParameters.Concat(context.GenericParameters).ToArray();
            var wrapper = Wrap(writer, target, optional, entry.Name + "_" + wrappers.Count, externalVarArg, parameters);
            wrappers.Add(wrapper);

            if (parameters.Length == 0)
            {
                instruction.Operand = wrapper;
            }
            else
            {
                var constructed = new GenericInstanceMethod(wrapper);
                foreach (var parameter in parameters)
                {
                    constructed.GenericArguments.Add(parameter);
                }

                instruction.Operand = constructed;
            }
        }

        Relocate(writer, [entry, .. wrappers], target);
    }
}
