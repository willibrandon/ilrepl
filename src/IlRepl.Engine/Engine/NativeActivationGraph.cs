using System.Reflection;
using System.Reflection.Emit;

namespace IlRepl.Engine;

/// <summary>
/// Finds modules reachable while preparing the selected body, excluding assemblies retained only for reconstruction.
/// </summary>
internal static class NativeActivationGraph
{
    /// <summary>
    /// Traverses body operands, signatures, field owners, base types, and closed generic arguments without activation.
    /// </summary>
    /// <param name="root">The actual implementation to prepare.</param>
    /// <param name="bindings">Captured trampolines and their bound implementations.</param>
    /// <returns>The assembly identities whose modules may be activated by preparation or address evidence.</returns>
    internal static HashSet<string> Assemblies(MethodBase root, IReadOnlyDictionary<MethodBase, MethodBase> bindings)
    {
        var assemblies = new HashSet<string>(StringComparer.Ordinal);
        var types = new HashSet<Type>();
        var methods = new HashSet<MethodBase>();
        var pending = new Queue<MethodBase>();
        var framework = Path.GetDirectoryName(typeof(object).Assembly.Location);
        pending.Enqueue(root);
        while (pending.TryDequeue(out var method))
        {
            if (!methods.Add(method))
            {
                continue;
            }

            if (methods.Count > 4096)
            {
                throw new ReplException("native inspection cannot establish a finite activation closure");
            }

            assemblies.Add(method.Module.Assembly.FullName!);
            if (method.DeclaringType is { } declaring)
            {
                VisitType(declaring);
            }

            if (method.IsGenericMethod)
            {
                foreach (var argument in method.GetGenericArguments())
                {
                    VisitType(argument);
                }
            }

            foreach (var parameter in method.GetParameters())
            {
                VisitType(parameter.ParameterType);
            }

            if (method is MethodInfo info)
            {
                VisitType(info.ReturnType);
            }

            if (bindings.TryGetValue(method, out var bodyMethod))
            {
                pending.Enqueue(bodyMethod);
            }

            if (Path.GetDirectoryName(method.Module.Assembly.Location) == framework || method.GetMethodBody() is not { } body)
            {
                continue;
            }

            foreach (var local in body.LocalVariables)
            {
                VisitType(local.LocalType);
            }

            foreach (var caught in body.ExceptionHandlingClauses.Where(clause => clause.Flags == ExceptionHandlingClauseOptions.Clause)
                .Select(clause => clause.CatchType).OfType<Type>())
            {
                VisitType(caught);
            }

            foreach (var instruction in IlReader.Read(body.GetILAsByteArray()!).Instructions)
            {
                if (instruction.Op.OperandType is not (OperandType.InlineMethod or OperandType.InlineType
                    or OperandType.InlineField or OperandType.InlineTok))
                {
                    continue;
                }

                var member = method.Module.ResolveMember(instruction.Operand.Token, method.DeclaringType?.GetGenericArguments(),
                    method.IsGenericMethod ? method.GetGenericArguments() : null);
                switch (member)
                {
                    case Type type: VisitType(type); break;
                    case MethodBase callee: pending.Enqueue(callee); break;
                    case FieldInfo field:
                        if (field.DeclaringType is { } owner)
                        {
                            VisitType(owner);
                        }

                        VisitType(field.FieldType);
                        break;
                }
            }
        }

        return assemblies;

        void VisitType(Type type)
        {
            if (!types.Add(type) || type.IsGenericParameter)
            {
                return;
            }

            if (type.HasElementType)
            {
                VisitType(type.GetElementType()!);
                return;
            }

            if (type.IsFunctionPointer)
            {
                VisitType(type.GetFunctionPointerReturnType());
                foreach (var parameter in type.GetFunctionPointerParameterTypes())
                {
                    VisitType(parameter);
                }

                return;
            }

            assemblies.Add(type.Assembly.FullName!);
            if (type.BaseType is { } parent)
            {
                VisitType(parent);
            }

            if (type.IsConstructedGenericType)
            {
                foreach (var argument in type.GetGenericArguments())
                {
                    VisitType(argument);
                }
            }
        }
    }
}
