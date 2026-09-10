using System.Reflection.Metadata;

namespace IlRepl.Engine.Binding;

/// <summary>
/// Resolves serialized reflection type names through the current binder without runtime name-resolution callbacks.
/// </summary>
internal static class SerializedTypeBinding
{
    /// <summary>
    /// Parses an attribute's assembly-qualified type name and binds its complete construction.
    /// </summary>
    /// <param name="text">The serialized reflection name.</param>
    /// <param name="scope">The binding scope.</param>
    /// <returns>The exact type symbol.</returns>
    public static TypeSymbol Parse(string text, IBindingScope scope)
    {
        if (!TypeName.TryParse(text, out var name))
        {
            throw new ReplException($"bad type name '{text}' in the attribute blob");
        }

        return Bind(name, scope);
    }

    private static TypeSymbol Bind(TypeName name, IBindingScope scope)
    {
        if (name.IsConstructedGenericType)
        {
            return TypeSymbol.Construct(Bind(name.GetGenericTypeDefinition(), scope),
                [.. name.GetGenericArguments().Select(argument => Bind(argument, scope))]);
        }

        if (name.IsArray)
        {
            var element = Bind(name.GetElementType(), scope);
            return name.IsSZArray ? TypeSymbol.SzArray(element) : TypeSymbol.Array(element, name.GetArrayRank(), [], []);
        }

        if (name.IsByRef)
        {
            return TypeSymbol.ByRef(Bind(name.GetElementType(), scope));
        }

        if (name.IsPointer)
        {
            return TypeSymbol.Pointer(Bind(name.GetElementType(), scope));
        }

        return scope.LookupType(Path(name), name.AssemblyName?.Name, 0, false).Type;
    }

    private static string Path(TypeName name) => name.IsNested
        ? Path(name.DeclaringType) + "/" + TypeName.Unescape(name.Name)
        : (name.Namespace.Length == 0 ? "" : TypeName.Unescape(name.Namespace) + ".") + TypeName.Unescape(name.Name);
}
