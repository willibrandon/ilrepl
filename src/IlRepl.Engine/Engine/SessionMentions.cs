using System.Reflection;
using IlRepl.Engine.Binding;

namespace IlRepl.Engine;

/// <summary>
/// Finds the session assemblies a body mentions: the types of its operands, locals, arguments,
/// and catch clauses, the declaring types of the members it reaches for, and every type inside
/// them (elements, generic arguments).
/// </summary>
public static class SessionMentions
{
    /// <summary>
    /// The session type assemblies a body depends on.
    /// </summary>
    /// <param name="state">The body.</param>
    /// <returns>The definitions, without duplicates.</returns>
    public static IEnumerable<DefinitionAssembly> Definitions(CellState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var found = new List<DefinitionAssembly>();
        foreach (var type in Types(state))
        {
            Note(type, found);
        }

        return found;
    }

    /// <summary>
    /// Every type a body mentions, constructed forms included.
    /// </summary>
    /// <param name="state">The body.</param>
    /// <returns>The types, with repeats.</returns>
    public static IEnumerable<Type> Types(CellState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        foreach (var local in state.Locals)
        {
            yield return local.Type;
            if (local.ExactType is not null)
            {
                foreach (var type in RuntimeSymbolTypes.Materialized(local.ExactType))
                {
                    yield return type;
                }
            }
        }

        foreach (var argument in state.Arguments)
        {
            yield return argument.Type;
            if (argument.ExactType is not null)
            {
                foreach (var type in RuntimeSymbolTypes.Materialized(argument.ExactType))
                {
                    yield return type;
                }
            }
        }

        foreach (var entry in state.Entries)
        {
            if (entry.Instruction?.ExactTypeOperand is { } exactType)
            {
                foreach (var type in RuntimeSymbolTypes.Materialized(exactType))
                {
                    yield return type;
                }
            }

            if (entry.Instruction?.Operand is CalliSignature { ExactSymbol: { } exactSignature })
            {
                foreach (var type in RuntimeSymbolTypes.Materialized(exactSignature.ReturnType))
                {
                    yield return type;
                }

                foreach (var parameter in exactSignature.Parameters)
                {
                    foreach (var type in RuntimeSymbolTypes.Materialized(parameter))
                    {
                        yield return type;
                    }
                }
            }

            if (entry.CatchType is { } catchType)
            {
                yield return catchType;
            }

            if (entry.Custom is { } custom)
            {
                yield return custom.AttributeType;
                foreach (var value in custom.FixedArguments.Concat(custom.NamedFields.Select(f => f.Value)).Concat(custom.NamedProperties.Select(p => p.Value)))
                {
                    if (value is Type mentioned)
                    {
                        yield return mentioned;
                    }
                }
            }

            switch (entry.Instruction?.Operand)
            {
                case Type type:
                    yield return type;
                    break;
                case FieldInfo field:
                    yield return field.DeclaringType!;
                    yield return field.FieldType;
                    break;
                case ResolvedMethod method:
                    if (method.DeclaringType is { } declaring)
                    {
                        yield return declaring;
                    }

                    yield return method.ReturnType;
                    foreach (var parameter in method.ParameterTypes)
                    {
                        yield return parameter;
                    }

                    foreach (var argument in method.InstantiationArguments)
                    {
                        yield return argument;
                    }

                    break;
                case CalliSignature signature:
                    yield return signature.ReturnType;
                    foreach (var parameter in signature.ParameterTypes.Concat(signature.OptionalParameterTypes ?? []))
                    {
                        yield return parameter;
                    }

                    break;
                default:
                    break;
            }
        }
    }

    private static void Note(Type? type, List<DefinitionAssembly> found)
    {
        while (type is not null)
        {
            if (type.HasElementType)
            {
                type = type.GetElementType();
                continue;
            }

            if (type.IsGenericParameter)
            {
                return;
            }

            if (type.IsConstructedGenericType)
            {
                foreach (var argument in type.GetGenericArguments())
                {
                    Note(argument, found);
                }

                type = type.GetGenericTypeDefinition();
            }

            if (TypeNameFormatter.IsFunctionPointer(type))
            {
                Note(type.GetFunctionPointerReturnType(), found);
                foreach (var parameter in type.GetFunctionPointerParameterTypes())
                {
                    Note(parameter, found);
                }

                return;
            }

            if (SessionAssemblies.TryGetDefinition(type.Assembly, out var definition) && !found.Contains(definition))
            {
                found.Add(definition);
            }

            return;
        }
    }
}
