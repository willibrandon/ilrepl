using System.Reflection;
using System.Reflection.Emit;

namespace IlRepl.Engine;

/// <summary>
/// Rejects copied type names whose generated spelling cannot preserve the source type's reported name.
/// </summary>
internal sealed partial class ImportedMethodFamily
{
    private const string TypeNameReason = "type name inspection cannot reproduce the original copied type name";

    private static bool IsTypeNameInspection(MethodBase method) => method.DeclaringType is { } type
        && type.Assembly == typeof(Type).Assembly
        && (typeof(Type).IsAssignableFrom(type)
                && method.Name is "get_Name" or "get_FullName" or "get_Namespace" or "get_AssemblyQualifiedName" or nameof(ToString)
            || typeof(MemberInfo).IsAssignableFrom(type) && method.Name == "get_Name");

    private string? TypeNameProblem(MethodBase method, object?[]? receivers)
    {
        if (!IsTypeNameInspection(method))
        {
            return null;
        }

        if (receivers is null || receivers.Length == 0)
        {
            return TypeNameReason;
        }

        foreach (var receiver in receivers)
        {
            if (receiver is null)
            {
                continue;
            }

            if (receiver is Type type)
            {
                if (TypeNameChanges(method.Name, type))
                {
                    return TypeNameReason;
                }
            }
            else if (method.Name != "get_Name" || receiver is not MemberInfo)
            {
                return TypeNameReason;
            }
        }

        return null;
    }

    private bool TypeNameChanges(string name, Type type) => name switch
    {
        "get_Name" => CopiedSimpleNameChanges(type),
        "get_Namespace" => CopiedNamespaceChanges(type),
        _ => ContainsCopiedType(type),
    };

    private bool CopiedSimpleNameChanges(Type type)
    {
        type = NameDefinition(type);
        if (type.IsGenericParameter || !_types.TryGetValue(DefinitionOf(type), out var path))
        {
            return false;
        }

        var separator = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('.'));
        return path[(separator + 1)..] != type.Name;
    }

    private bool CopiedNamespaceChanges(Type type)
    {
        type = NameDefinition(type);
        if (type.IsGenericParameter || !_types.TryGetValue(DefinitionOf(type), out var path))
        {
            return false;
        }

        return path[..path.LastIndexOf('.')] != type.Namespace;
    }

    private static Type NameDefinition(Type type)
    {
        while (type.HasElementType)
        {
            type = type.GetElementType()!;
        }

        return type.IsConstructedGenericType ? type.GetGenericTypeDefinition() : type;
    }

    private void ValidateTypeNameReference(ReflectionValueResolver values, MethodEditBody body, int position, Instruction instruction)
    {
        if (instruction.Operand is not ResolvedMethod resolved || instruction.Op == OpCodes.Ldtoken)
        {
            return;
        }

        var target = resolved.Method ?? _pinned[resolved.Definition!.Name];
        if (!IsTypeNameInspection(target))
        {
            return;
        }

        var receivers = instruction.Op == OpCodes.Ldftn ? null : values.Argument(body, position, -1);
        if (TypeNameProblem(target, receivers) is { } problem)
        {
            RejectReflection(body, instruction, target, problem);
        }
    }
}
