using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;

namespace IlRepl.Engine;

/// <summary>
/// Translates reflection names at a copied lookup call while leaving the caller's strings intact.
/// </summary>
internal static partial class CopiedTypeNames
{
    /// <summary>
    /// Remaps copied types throughout a reflection name using the runtime's type-name grammar.
    /// </summary>
    /// <param name="name">The name supplied to Type.GetType.</param>
    /// <param name="ignoreCase">Whether the lookup ignores case.</param>
    /// <param name="context">The original caller's assembly identity.</param>
    /// <param name="names">Triples containing the original name, original assembly, and copied qualified name.</param>
    /// <param name="preserveAssemblies">Whether a user-supplied resolver controls assembly-qualified names.</param>
    /// <returns>The translated name, or the original input when it cannot be parsed.</returns>
    internal static string? Translate(string? name, bool ignoreCase, string context, string[] names, bool preserveAssemblies)
    {
        if (name is null || !TypeName.TryParse(name.AsSpan(), out var parsed)) return name;
        return TranslateType(parsed, ignoreCase, context, names, preserveAssemblies).AssemblyQualifiedName;
    }

    /// <summary>
    /// Remaps names looked up within a copied assembly or module while preserving invalid outer assembly qualifiers.
    /// </summary>
    /// <param name="name">The name supplied to the instance lookup.</param>
    /// <param name="ignoreCase">Whether the lookup ignores case.</param>
    /// <param name="names">The original and copied type identities.</param>
    /// <returns>The translated name without an outer assembly qualifier, or the unchanged invalid input.</returns>
    internal static string? TranslateScoped(string? name, bool ignoreCase, string[] names)
    {
        if (name is null || !TypeName.TryParse(name.AsSpan(), out var parsed) || parsed.AssemblyName is not null) return name;
        return TranslateType(parsed, ignoreCase, null, names, false).FullName;
    }

    private static TypeName TranslateType(TypeName type, bool ignoreCase, string? context, string[] names, bool preserveAssemblies)
    {
        if (type.IsArray || type.IsPointer || type.IsByRef)
        {
            var element = TranslateType(type.GetElementType(), ignoreCase, context, names, preserveAssemblies);
            if (type.IsSZArray) return element.MakeSZArrayTypeName();
            if (type.IsArray) return element.MakeArrayTypeName(type.GetArrayRank());
            return type.IsPointer ? element.MakePointerTypeName() : element.MakeByRefTypeName();
        }

        if (type.IsConstructedGenericType)
        {
            var definition = TranslateType(type.GetGenericTypeDefinition(), ignoreCase, context, names, preserveAssemblies);
            var arguments = ImmutableArrayExtensions.ToArray(type.GetGenericArguments());
            for (var index = 0; index < arguments.Length; index++)
            {
                arguments[index] = TranslateType(arguments[index], ignoreCase, context, names, preserveAssemblies);
            }

            return definition.MakeGenericTypeName(ImmutableArray.Create(arguments));
        }

        if (preserveAssemblies && type.AssemblyName is not null) return type;
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string? match = null;
        for (var index = 0; index < names.Length; index += 3)
        {
            if (!string.Equals(type.FullName, names[index], comparison)) continue;
            var assembly = type.AssemblyName;
            if (assembly is null ? context is not null && names[index + 1] != context
                : !AssemblyName.ReferenceMatchesDefinition(new AssemblyName(assembly.FullName), new AssemblyName(names[index + 1])))
            {
                continue;
            }

            if (assembly?.Version is { } version && version > new AssemblyName(names[index + 1]).Version) continue;

            if (match is not null && match != names[index + 2]) throw new AmbiguousMatchException();
            match = names[index + 2];
        }

        if (match is null) return type;
        var result = TypeName.Parse(match.AsSpan());
        return preserveAssemblies ? result.WithAssemblyName(null) : result;
    }
}
