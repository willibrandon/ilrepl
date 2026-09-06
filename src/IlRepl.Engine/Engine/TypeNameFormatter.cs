using System.Reflection.Emit;
using System.Text;

namespace IlRepl.Engine;

/// <summary>
/// Renders types the way the prompt and ILAsm output want to see them.
/// </summary>
public static class TypeNameFormatter
{
    /// <summary>
    /// A short, IL-flavored name: <c>int32</c>, <c>string</c>, <c>List&lt;int32&gt;</c>, <c>int32[]</c>, <c>int32&amp;</c>.
    /// </summary>
    /// <param name="type">The type, or null for an unknown stack entry.</param>
    /// <returns>The display name.</returns>
    public static string Pretty(Type? type)
    {
        if (type is null)
        {
            return "?";
        }

        var keyword = TypeParser.PrimitiveKeyword(type);
        if (keyword is not null)
        {
            return keyword;
        }

        if (type.IsGenericParameter)
        {
            return (type.DeclaringMethod is null ? "!" : "!!") + type.Name;
        }

        if (type.IsByRef)
        {
            return Pretty(type.GetElementType()) + "&";
        }

        if (type.IsPointer)
        {
            return Pretty(type.GetElementType()) + "*";
        }

        if (type.IsArray)
        {
            var rank = type.GetArrayRank();
            return Pretty(type.GetElementType()) + "[" + new string(',', rank - 1) + "]";
        }

        if (type.IsGenericType)
        {
            var sb = new StringBuilder();
            var name = type.Name;
            var tick = name.IndexOf('`', StringComparison.Ordinal);
            sb.Append(tick > 0 ? name[..tick] : name);
            sb.Append('<');
            sb.Append(string.Join(", ", type.GetGenericArguments().Select(Pretty)));
            sb.Append('>');
            return sb.ToString();
        }

        if (type.IsNested && type.DeclaringType is not null)
        {
            return Pretty(type.DeclaringType) + "/" + type.Name;
        }

        return type.Name;
    }

    /// <summary>
    /// The fully qualified ILAsm spelling of a type, suitable for <c>.il</c> output.
    /// </summary>
    /// <param name="type">The type.</param>
    /// <returns>The ILAsm type reference.</returns>
    public static string IlAsm(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var keyword = TypeParser.PrimitiveKeyword(type);
        if (keyword is not null)
        {
            return keyword;
        }

        if (type.IsGenericParameter)
        {
            return (type.DeclaringMethod is null && type is not GenericTypeParameterBuilder ? "!" : "!!") + type.Name;
        }

        if (type.IsByRef)
        {
            return IlAsm(type.GetElementType()!) + "&";
        }

        if (type.IsPointer)
        {
            return IlAsm(type.GetElementType()!) + "*";
        }

        if (type.IsArray)
        {
            return IlAsm(type.GetElementType()!) + "[" + new string(',', type.GetArrayRank() - 1) + "]";
        }

        var kind = type.IsValueType ? "valuetype " : "class ";
        var assembly = AssemblyReferenceName(type);
        var definition = type.IsGenericType ? type.GetGenericTypeDefinition() : type;
        var full = (definition.FullName ?? definition.Name).Replace('+', '/');
        if (type.IsGenericType)
        {
            full += "<" + string.Join(", ", type.GetGenericArguments().Select(IlAsm)) + ">";
        }

        return $"{kind}[{assembly}]{full}";
    }

    /// <summary>
    /// The ILAsm spelling of a type reference in a member position, without the <c>class</c>/<c>valuetype</c> prefix.
    /// </summary>
    /// <param name="type">The declaring type.</param>
    /// <returns>The reference text.</returns>
    public static string IlAsmDeclaring(Type type)
    {
        var text = IlAsm(type);
        if (text.StartsWith("class ", StringComparison.Ordinal))
        {
            return text[6..];
        }

        return text.StartsWith("valuetype ", StringComparison.Ordinal) ? text[10..] : text;
    }

    /// <summary>
    /// The assembly name ILAsm should reference for a type. Core types map to the <c>System.Runtime</c> facade.
    /// </summary>
    /// <param name="type">The type.</param>
    /// <returns>The assembly name.</returns>
    public static string AssemblyReferenceName(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var name = type.Assembly.GetName().Name ?? "System.Runtime";
        return name == "System.Private.CoreLib" ? "System.Runtime" : name;
    }
}
