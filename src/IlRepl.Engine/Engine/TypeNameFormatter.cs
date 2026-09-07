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

        if (IsFunctionPointer(type))
        {
            return IlSignatureRenderer.Pretty(IlSignature.FromType(type));
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
            return (type.DeclaringMethod is null ? "!" : "!!") + type.Name;
        }

        if (type.IsByRef)
        {
            return IlAsm(type.GetElementType()!) + "&";
        }

        if (type.IsPointer)
        {
            return IlAsm(type.GetElementType()!) + "*";
        }

        if (IsFunctionPointer(type))
        {
            // Reflection describes a function pointer's signature; the signature renderer spells it.
            return IlSignatureRenderer.IlAsm(IlSignature.FromType(type));
        }

        if (type.IsArray)
        {
            return IlAsm(type.GetElementType()!) + ArraySuffix(type);
        }

        if (type.IsGenericTypeDefinition)
        {
            // An open definition is a bare reference, List`1, never an instantiation over its own
            // parameters: ilasm encodes the keyword form as a TypeSpec, which the runtime refuses for
            // a definition, and the bare form as the TypeRef the C# compiler writes.
            var definitionText = IlAsmDefinition(type);
            return definitionText.StartsWith("class ", StringComparison.Ordinal) ? definitionText[6..] : definitionText.StartsWith("valuetype ", StringComparison.Ordinal) ? definitionText[10..] : definitionText;
        }

        var full = IlAsmDefinition(type);
        if (type.IsGenericType)
        {
            full += "<" + string.Join(", ", type.GetGenericArguments().Select(IlAsm)) + ">";
        }

        return full;
    }

    /// <summary>
    /// True for a function pointer type. A builder or a placeholder type answers the question with
    /// <see cref="NotImplementedException"/>, and none of those is a function pointer.
    /// </summary>
    /// <param name="type">The type.</param>
    /// <returns>True when the runtime describes the type as a function pointer.</returns>
    public static bool IsFunctionPointer(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        try
        {
            return type.IsFunctionPointer;
        }
        catch (Exception ex) when (ex is NotImplementedException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// The ILAsm spelling of a type's definition, without generic arguments: <c>class [System.Collections]System.Collections.Generic.List`1</c>.
    /// </summary>
    /// <param name="type">The type, or an instantiation of it.</param>
    /// <returns>The reference text with its <c>class</c>/<c>valuetype</c> word.</returns>
    public static string IlAsmDefinition(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var kind = type.IsValueType ? "valuetype " : "class ";
        var definition = type.IsGenericType ? type.GetGenericTypeDefinition() : type;
        var full = QualifiedName(definition);

        // A session type lives in the module being rendered, so it is named without an assembly.
        return TypeRelations.IsSessionType(definition) ? kind + full : $"{kind}[{AssemblyReferenceName(type)}]{full}";
    }

    /// <summary>
    /// A type name as ILAsm reads it: quoted when the lexer would not take it as a name, with an
    /// arity suffix left outside the quotes because ILAsm reads <c>List`1</c> as one name.
    /// </summary>
    /// <param name="name">The simple type name.</param>
    /// <returns>The name, quoted when needed.</returns>
    public static string IlAsmTypeName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var tick = name.IndexOf('`', StringComparison.Ordinal);
        if (tick > 0 && name[(tick + 1)..].All(char.IsDigit))
        {
            // A name that needs quotes takes its arity inside them: ILAsm reads '<>c`1', not '<>c'`1.
            var quoted = IlAsmIdentifier(name[..tick]);
            return quoted.StartsWith('\'') ? "'" + name + "'" : name;
        }

        return IlAsmIdentifier(name);
    }

    /// <summary>
    /// The namespace-qualified, nesting-qualified name of a type definition, each segment quoted on
    /// its own when it must be: <c>System.Collections.Generic.List`1</c>, <c>Program/'&lt;&gt;c'</c>.
    /// </summary>
    /// <param name="definition">The type definition.</param>
    /// <returns>The qualified name without an assembly.</returns>
    public static string QualifiedName(Type definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.IsNested && definition.DeclaringType is { } declaring)
        {
            return QualifiedName(declaring) + "/" + IlAsmTypeName(Unescape(definition.Name));
        }

        var name = IlAsmTypeName(Unescape(definition.Name));
        return string.IsNullOrEmpty(definition.Namespace) ? name : string.Join(".", Unescape(definition.Namespace).Split('.').Select(IlAsmIdentifier)) + "." + name;
    }

    /// <summary>
    /// Reflection escapes the characters its own type-name grammar reserves, writing a comma in a
    /// name as <c>\,</c>; the metadata name has no backslash, and neither does ILAsm's quoted form.
    /// </summary>
    private static string Unescape(string name) => name.Contains('\\') ? name.Replace("\\", "", StringComparison.Ordinal) : name;

    /// <summary>
    /// The ILAsm spelling of a type reference in a member position, without the <c>class</c>/<c>valuetype</c> prefix.
    /// </summary>
    /// <param name="type">The declaring type.</param>
    /// <returns>The reference text.</returns>
    public static string IlAsmDeclaring(Type type)
    {
        var text = IlAsm(type);
        if (type.IsGenericType && !type.IsGenericTypeDefinition)
        {
            // A generic instantiation keeps its prefix in a member position: class Box`1<int32>::V.
            return text;
        }

        if (text.StartsWith("class ", StringComparison.Ordinal))
        {
            return text[6..];
        }

        return text.StartsWith("valuetype ", StringComparison.Ordinal) ? text[10..] : text;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, string> FacadeNames = new();

    /// <summary>
    /// The assembly name ILAsm should reference for a type. A type that lives in
    /// <c>System.Private.CoreLib</c> is named by the facade that exports it, which is what a
    /// reference must bind through: <c>System.Runtime</c> for <c>string</c>, <c>System.Collections</c>
    /// for <c>List`1</c>. A reference through a facade that does not export the type would assemble
    /// and then fail to load.
    /// </summary>
    /// <param name="type">The type.</param>
    /// <returns>The assembly name.</returns>
    public static string AssemblyReferenceName(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var name = type.Assembly.GetName().Name ?? "System.Runtime";
        if (name != "System.Private.CoreLib")
        {
            return name;
        }

        var definition = type;
        while (definition.HasElementType)
        {
            definition = definition.GetElementType()!;
        }

        if (definition.IsGenericType && !definition.IsGenericTypeDefinition)
        {
            definition = definition.GetGenericTypeDefinition();
        }

        while (definition.IsNested && definition.DeclaringType is { } declaring)
        {
            definition = declaring;
        }

        return definition.FullName is null ? "System.Runtime" : FacadeNames.GetOrAdd(definition, FacadeExporting);
    }

    private static string FacadeExporting(Type definition)
    {
        // System.Runtime first, the rest alphabetically, so the spelling is stable: the first loaded
        // facade whose exported types include the definition names it.
        var facades = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && a.GetName().Name is { } n && n.StartsWith("System.", StringComparison.Ordinal) && n != "System.Private.CoreLib")
            .OrderBy(a => a.GetName().Name == "System.Runtime" ? 0 : 1)
            .ThenBy(a => a.GetName().Name, StringComparer.Ordinal);
        foreach (var facade in facades)
        {
            Type? exported;
            try
            {
                exported = facade.GetType(definition.FullName!, throwOnError: false);
            }
            catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or TypeLoadException or BadImageFormatException)
            {
                continue;
            }

            if (exported == definition)
            {
                return facade.GetName().Name!;
            }
        }

        return "System.Runtime";
    }
}
