using System.Reflection;
using IlRepl.Protocol;

namespace IlRepl.Engine;

/// <summary>
/// Retains complete copied type contexts when runtime reflection can reach members without static IL references.
/// </summary>
internal sealed partial class ImportedMethodFamily
{
    private string? _reflectionLocation;

    private void ScanReflection()
    {
        if (_reflectionLocation is null)
        {
            return;
        }

        foreach (var type in _types.Keys.ToArray())
        {
            foreach (var nested in type.GetNestedTypes(Declared))
            {
                AddType(nested);
            }

            foreach (var member in type.GetMethods(Declared).Cast<MethodBase>().Concat(type.GetConstructors(Declared)))
            {
                AddMethod(member);
                _dependencies.Add(new EditDependency(MemberResolver.Describe(member), member.Module.Assembly.FullName!,
                    _reflectionLocation + ": reflective access", "copied") { Access = MemberAccess.AccessWord(member.Attributes) });
            }
        }
    }

    private static bool ReflectsMembers(MethodBase method)
    {
        var type = method.DeclaringType;
        return type == typeof(Activator) || type == typeof(object) && method.Name == nameof(GetType)
            || type is not null && (typeof(Type).IsAssignableFrom(type) || type == typeof(IReflect))
                && (method.Name.StartsWith("Get", StringComparison.Ordinal) || method.Name.StartsWith("Find", StringComparison.Ordinal)
                    || method.Name.StartsWith("get_Declared", StringComparison.Ordinal)
                    || method.Name == nameof(Type.InvokeMember))
            || type?.FullName == "System.Reflection.RuntimeReflectionExtensions";
    }
}
