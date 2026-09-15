using System.Text;
using Mono.Cecil;

namespace IlRepl.Engine;

/// <summary>
/// Writes reflection type names for metadata strings after their dependencies have been remapped.
/// </summary>
internal static class CecilSerializedTypeName
{
    /// <summary>
    /// Detects copied declarations anywhere in a serialized type, including generic arguments.
    /// </summary>
    /// <param name="type">The imported type.</param>
    /// <param name="module">The destination module.</param>
    /// <returns>Whether the type's serialized identity needs remapping.</returns>
    internal static bool References(TypeReference type, ModuleDefinition module) => type.Scope == module
        || type is GenericInstanceType generic && generic.GenericArguments.Any(argument => References(argument, module))
        || type is TypeSpecification specification && References(specification.ElementType, module);

    /// <summary>
    /// Formats a complete type identity with escaped names and assembly-qualified generic arguments.
    /// </summary>
    /// <param name="type">The imported type.</param>
    /// <returns>The name accepted by runtime type resolution.</returns>
    internal static string Format(TypeReference type)
    {
        var assembly = type.Scope switch
        {
            AssemblyNameReference reference => reference.FullName,
            ModuleDefinition module => module.Assembly.Name.FullName,
            _ => type.Module.Assembly.Name.FullName,
        };
        return Name(type) + ", " + assembly;
    }

    /// <summary>
    /// Formats a type name for lookup within its assembly, retaining qualifiers on generic arguments.
    /// </summary>
    /// <param name="type">The imported type.</param>
    /// <returns>The escaped reflection name without its outer assembly qualifier.</returns>
    internal static string Name(TypeReference type) => type switch
    {
        GenericInstanceType generic => Name(generic.ElementType) + "["
            + string.Join(",", generic.GenericArguments.Select(argument => "[" + Format(argument) + "]")) + "]",
        ArrayType array => Name(array.ElementType) + (array.IsVector ? "[]" : array.Rank == 1 ? "[*]"
            : "[" + new string(',', array.Rank - 1) + "]"),
        ByReferenceType reference => Name(reference.ElementType) + "&",
        PointerType pointer => Name(pointer.ElementType) + "*",
        _ => (type.DeclaringType is { } owner ? Name(owner) + "+" : type.Namespace.Length == 0 ? "" : Escape(type.Namespace) + ".")
            + Escape(type.Name),
    };

    private static string Escape(string name)
    {
        var result = new StringBuilder();
        foreach (var character in name)
        {
            if (character is '\\' or '+' or ',' or '[' or ']' or '&' or '*')
            {
                result.Append('\\');
            }

            result.Append(character);
        }

        return result.ToString();
    }
}
