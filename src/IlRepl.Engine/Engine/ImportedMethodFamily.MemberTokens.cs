using System.Reflection;
using System.Reflection.Emit;

namespace IlRepl.Engine;

/// <summary>
/// Rejects numeric member tokens for copied or unproven receivers while preserving unchanged external metadata inspection.
/// </summary>
internal sealed partial class ImportedMethodFamily
{
    private const string MemberTokenReason = "member token inspection cannot reproduce the original metadata tokens";

    private static bool IsMemberTokenInspection(MethodBase method) => method.DeclaringType is { } type
        && type.Assembly == typeof(MemberInfo).Assembly && method.Name == "get_MetadataToken"
        && (typeof(MemberInfo).IsAssignableFrom(type) || typeof(ParameterInfo).IsAssignableFrom(type));

    private string? MemberTokenProblem(object?[]? receivers)
    {
        if (receivers is null || receivers.Length == 0)
        {
            return MemberTokenReason;
        }

        foreach (var receiver in receivers)
        {
            var member = receiver is ParameterInfo parameter ? parameter.Member : receiver as MemberInfo;
            if (member is null)
            {
                return MemberTokenReason;
            }

            if (CopiedMemberToken(member))
            {
                return MemberTokenReason;
            }
        }

        return null;
    }

    private bool CopiedMemberToken(MemberInfo member)
    {
        if (member is Type type)
        {
            if (type.HasElementType || type.IsGenericParameter)
            {
                if ((type.MetadataToken & 0x00ffffff) == 0)
                {
                    return false;
                }

                var owner = type.HasElementType ? type.GetElementType()
                    : (MemberInfo?)type.DeclaringMethod ?? type.DeclaringType;
                return owner is null || CopiedMemberToken(owner);
            }

            return _types.ContainsKey(DefinitionOf(type));
        }

        return member.DeclaringType is null || CopiedMemberToken(member.DeclaringType)
            || member is MethodBase method && _methods.ContainsKey(IlAsmRenderer.DefinitionOf(method));
    }

    private void ValidateMemberTokenReference(ReflectionValueResolver values, MethodEditBody body, int position, Instruction instruction)
    {
        if (instruction.Operand is not ResolvedMethod resolved || instruction.Op == OpCodes.Ldtoken)
        {
            return;
        }

        var target = resolved.Method ?? _pinned[resolved.Definition!.Name];
        if (!IsMemberTokenInspection(target))
        {
            return;
        }

        var receivers = instruction.Op == OpCodes.Ldftn ? null : values.Argument(body, position, -1);
        if (AssemblyInspectionProblem(target, receivers) is { } problem)
        {
            RejectReflection(body, instruction, target, problem);
        }
    }

    private static object?[]? MemberTokenReceiver(ReflectionValueResolver values, MethodEditBody body, int position, MethodBase target)
    {
        if (target.Name == nameof(PropertyInfo.GetValue) && typeof(PropertyInfo).IsAssignableFrom(target.DeclaringType!))
        {
            return values.Argument(body, position, 0);
        }

        if (target.Name == nameof(Type.InvokeMember) && (typeof(Type).IsAssignableFrom(target.DeclaringType!)
            || target.DeclaringType == typeof(IReflect)))
        {
            var parameters = target.GetParameters();
            var receiver = Array.FindIndex(parameters, parameter => parameter.Name == "target");
            return receiver < 0 ? null : values.Argument(body, position, receiver);
        }

        return InvocationReceiver(values, body, position, target);
    }
}
