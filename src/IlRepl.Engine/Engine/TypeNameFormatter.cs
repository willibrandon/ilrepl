using System.Reflection.Emit;
using System.Text;

namespace IlRepl.Engine;

/// <summary>
/// Renders types the way the prompt and ILAsm output want to see them.
/// </summary>
public static class TypeNameFormatter
{
    /// <summary>
    /// Every identifier-shaped word the ILAsm lexer reserves: its keyword table
    /// (dotnet/runtime, src/coreclr/inc/il_kywd.h) and every opcode name and alias
    /// (src/coreclr/inc/opcode.def). A name in this set must be quoted to be read as a name.
    /// </summary>
    private static readonly HashSet<string> IlAsmKeywords = new(StringComparer.Ordinal)
    {
        "abstract", "add", "aggressiveinlining", "aggressiveoptimization", "algorithm", "alignment", "amd64", "and",
        "ansi", "any", "arglist", "arm", "arm64", "array", "as", "assembly",
        "assert", "async", "at", "auto", "autochar", "beforefieldinit", "beq", "bestfit",
        "bge", "bgt", "ble", "blob", "blob_object", "blt", "bool", "box",
        "br", "break", "brfalse", "brinst", "brnull", "brtrue", "brzero", "bstr",
        "byreflike", "bytearray", "byvalstr", "call", "callconv", "calli", "callmostderived", "callvirt",
        "carray", "castclass", "catch", "cdecl", "ceq", "cf", "cgt", "char",
        "charmaperror", "cil", "ckfinite", "class", "clsid", "clt", "codelabel", "constraint",
        "cpblk", "cpobj", "currency", "custom", "date", "decimal", "default", "demand",
        "deny", "div", "dup", "endfault", "endfilter", "endfinally", "endmac", "enum",
        "error", "explicit", "extended", "extends", "extern", "false", "famandassem", "family",
        "famorassem", "fastcall", "fault", "field", "filetime", "filter", "final", "finally",
        "fixed", "flags", "float", "float32", "float64", "forwarder", "forwardref", "fromunmanaged",
        "handler", "hidebysig", "hresult", "idispatch", "iidparam", "il", "illegal", "implements",
        "import", "in", "inheritcheck", "init", "initblk", "initobj", "initonly", "instance",
        "int", "int16", "int32", "int64", "int8", "interface", "internalcall", "isinst",
        "iunknown", "jmp", "lasterr", "ldarg", "ldarga", "ldelem", "ldelema", "ldfld",
        "ldflda", "ldftn", "ldlen", "ldloc", "ldloca", "ldnull", "ldobj", "ldsfld",
        "ldsflda", "ldstr", "ldtoken", "ldvirtftn", "leave", "legacy", "library", "linkcheck",
        "literal", "localloc", "lpstr", "lpstruct", "lptstr", "lpvoid", "lpwstr", "managed",
        "marshal", "mdtoken", "method", "mkrefany", "modopt", "modreq", "mul", "native",
        "neg", "nested", "newarr", "newobj", "newslot", "noinlining", "nomangle", "nometadata",
        "noncasdemand", "noncasinheritance", "noncaslinkdemand", "nooptimization", "nop", "noplatform", "not", "notserialized",
        "null", "nullref", "object", "objectref", "off", "on", "opt", "optil",
        "or", "out", "permitonly", "pinned", "pinvokeimpl", "pop", "prefix1", "prefix2",
        "prefix3", "prefix4", "prefix5", "prefix6", "prefix7", "prefixref", "prejitdeny", "prejitgrant",
        "preservesig", "private", "privatescope", "property", "public", "record", "refany", "refanytype",
        "refanyval", "rem", "reqmin", "reqopt", "reqrefuse", "reqsecobj", "request", "ret",
        "retainappdomain", "retargetable", "rethrow", "rtspecialname", "runtime", "safearray", "sealed", "sequential",
        "serializable", "shl", "shr", "sizeof", "specialname", "starg", "static", "stdcall",
        "stelem", "stfld", "stloc", "stobj", "storage", "stored_object", "stream", "streamed_object",
        "strict", "string", "struct", "stsfld", "sub", "switch", "synchronized", "syschar",
        "sysstring", "tbstr", "thiscall", "throw", "tls", "to", "true", "type",
        "typedref", "uint", "uint16", "uint32", "uint64", "uint8", "unbox", "unicode",
        "unmanaged", "unmanagedexp", "unsigned", "unused", "userdefined", "value", "valuetype", "vararg",
        "variant", "vector", "virtual", "void", "wchar", "winapi", "windowsruntime", "with",
        "x86", "xor",
    };

    private static string ArraySuffix(Type array)
    {
        // A vector is int32[]; a rank-1 array with bounds is int32[0...], which ILAsm and the
        // runtime keep distinct from the vector.
        var rank = array.GetArrayRank();
        return rank == 1 && !array.IsSZArray ? "[0...]" : "[" + new string(',', rank - 1) + "]";
    }

    /// <summary>
    /// Renders a name the way ILAsm needs it: quoted when the lexer reserves it or it is not a
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
            return Pretty(type.GetElementType()) + ArraySuffix(type);
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
            return IlAsm(type.GetElementType()!) + ArraySuffix(type);
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
