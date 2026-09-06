using System.Reflection.Emit;
using System.Text;

namespace IlRepl.Engine;

/// <summary>
/// Renders types the way the prompt and ILAsm output want to see them.
/// </summary>
public static class TypeNameFormatter
{
    private static readonly HashSet<string> IlAsmKeywords = new(StringComparer.Ordinal)
    {
        "abstract", "aggressiveinlining", "algorithm", "alignment", "ansi", "any", "array", "as", "assembly", "assert", "at", "auto", "autochar",
        "beforefieldinit", "blob", "bool", "boxed", "bstr", "bytearray", "byvalstr", "carray", "catch", "cdecl", "cf", "char", "cil", "class",
        "clsid", "const", "constrained", "culture", "currency", "custom", "data", "date", "decimal", "default", "demand", "deny", "disabled",
        "enum", "error", "event", "explicit", "extends", "extern", "false", "family", "famandassem", "famorassem", "fastcall", "fault", "field",
        "filetime", "filter", "final", "finally", "fixed", "flags", "float", "float32", "float64", "forwardref", "fromunmanaged", "handler",
        "hash", "hidebysig", "hresult", "idispatch", "il", "illegal", "implements", "implicitcom", "implicitres", "import", "in", "inheritcheck",
        "init", "initonly", "instance", "int", "int8", "int16", "int32", "int64", "interface", "internalcall", "iunknown", "lasterr", "lcid",
        "linkcheck", "literal", "locale", "localloc", "lpstr", "lpstruct", "lptstr", "lpvoid", "lpwstr", "managed", "marshal", "method",
        "modopt", "modreq", "module", "native", "nested", "newslot", "noappdomain", "noinlining", "nomachine", "nomangle", "nometadata",
        "noncasdemand", "noncasinheritance", "noncaslinkdemand", "noprocess", "not_in_gc_heap", "notremotable", "notserialized", "null",
        "nullref", "object", "objectref", "opt", "optil", "out", "overrides", "pack", "permitonly", "pinned", "pinvokeimpl", "platformapi",
        "prefix1", "prefix2", "prefix3", "prefix4", "prefix5", "prefix6", "prefix7", "prefixref", "prejitdeny", "prejitgrant", "preservesig",
        "private", "privatescope", "property", "protected", "public", "readonly", "record", "refany", "reqmin", "reqopt", "reqrefuse",
        "request", "retargetable", "retval", "rtspecialname", "runtime", "safearray", "sealed", "sequential", "serializable", "size",
        "specialname", "static", "stdcall", "storage", "stored", "stream", "streamed", "string", "struct", "synchronized", "syschar",
        "sysstring", "tbstr", "thiscall", "tls", "to", "true", "typedref", "uint", "uint8", "uint16", "uint32", "uint64", "unicode", "union",
        "unmanaged", "unmanagedexp", "unsigned", "unused", "userdefined", "value", "valuetype", "vararg", "variant", "vector", "virtual",
        "void", "wchar", "winapi", "with",
    };

    /// <summary>
    /// Renders a name the way ILAsm needs it: quoted when it is an opcode or a keyword, or not a
    /// plain identifier, so a method called <c>add</c> or a parameter called <c>value</c> assembles.
    /// </summary>
    /// <param name="name">The name.</param>
    /// <returns>The name, quoted when needed.</returns>
    public static string IlAsmIdentifier(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return IlAsmKeywords.Contains(name) || OpcodeTable.ByName.ContainsKey(name) || !InstructionParser.IsIdentifier(name) ? "'" + name + "'" : name;
    }

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
