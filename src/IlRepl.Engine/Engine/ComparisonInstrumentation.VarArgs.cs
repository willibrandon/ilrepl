using Mono.Cecil;

namespace IlRepl.Engine;

/// <summary>
/// Supplies typed observation wrappers that forward every optional argument to a managed-vararg method.
/// </summary>
internal static partial class ComparisonInstrumentation
{
    internal static void CompleteVarArgCalls(CecilWriter writer, MethodDefinition target, MethodDefinition entry)
    {
        if (target.CallingConvention != MethodCallingConvention.VarArg)
        {
            return;
        }

        var calls = writer.Module.GetTypes().SelectMany(type => type.Methods).Where(method => method.HasBody)
            .SelectMany(method => method.Body.Instructions).Where(instruction => instruction.Operand is MethodReference reference
                && reference.Name == entry.Name && reference.DeclaringType.FullName == entry.DeclaringType.FullName).ToArray();
        var wrappers = new Dictionary<string, MethodDefinition>(StringComparer.Ordinal);
        foreach (var instruction in calls)
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

            var key = string.Join(";", optional.Select(type => type.FullName + ", " + type.Scope));
            if (!wrappers.TryGetValue(key, out var wrapper))
            {
                wrapper = Wrap(writer, target, optional, entry.Name + "_" + wrappers.Count);
                wrappers.Add(key, wrapper);
            }

            instruction.Operand = wrapper;
        }
    }
}
