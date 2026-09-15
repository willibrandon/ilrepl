using IlRepl.Protocol;
using Mono.Cecil;

namespace IlRepl.Engine;

/// <summary>
/// Names closed generic arguments using their identities in the exported comparison assembly.
/// </summary>
public static partial class ComparisonCapture
{
    private static void CaptureOriginalArgument(Type type, Session session, Dictionary<string, ComparisonAssembly> dependencies)
    {
        if (type.HasElementType)
        {
            CaptureOriginalArgument(type.GetElementType()!, session, dependencies);
            return;
        }

        if (type.IsConstructedGenericType)
        {
            foreach (var argument in type.GetGenericArguments())
            {
                CaptureOriginalArgument(argument, session, dependencies);
            }
        }

        CaptureDependency(type.Assembly.FullName!, session, dependencies);
    }

    private static string ArgumentName(TypeReference type)
    {
        var assembly = type.Scope switch
        {
            AssemblyNameReference reference => reference.FullName,
            ModuleDefinition module => module.Assembly.Name.FullName,
            _ => throw new ReplException($"generic argument {type.FullName} has no loadable assembly identity"),
        };
        return ReflectionName(type) + ", " + assembly;
    }

    private static string ReflectionName(TypeReference type) => type switch
    {
        GenericInstanceType generic => ReflectionName(generic.ElementType) + "[["
            + string.Join("],[", generic.GenericArguments.Select(ArgumentName)) + "]]",
        ArrayType array => ReflectionName(array.ElementType) + (array.IsVector ? "[]"
            : array.Rank == 1 ? "[*]" : "[" + new string(',', array.Rank - 1) + "]"),
        ByReferenceType reference => ReflectionName(reference.ElementType) + "&",
        PointerType pointer => ReflectionName(pointer.ElementType) + "*",
        _ => (type.DeclaringType is { } parent ? ReflectionName(parent) + "+"
            : type.Namespace.Length == 0 ? "" : EscapeName(type.Namespace) + ".") + EscapeName(type.Name),
    };

    private static string EscapeName(string name) => name.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace(",", "\\,", StringComparison.Ordinal).Replace("+", "\\+", StringComparison.Ordinal)
        .Replace("[", "\\[", StringComparison.Ordinal).Replace("]", "\\]", StringComparison.Ordinal)
        .Replace("&", "\\&", StringComparison.Ordinal).Replace("*", "\\*", StringComparison.Ordinal);
}
