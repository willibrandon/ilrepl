namespace IlRepl.Engine.Binding;

/// <summary>
/// The primitive type keywords of CIL and the C# spellings the prompt accepts for them, with the
/// canonical keyword each one names and the CoreLib type behind it. Every spelling of one type
/// binds to one symbol.
/// </summary>
public static class CilPrimitives
{
    private static readonly Dictionary<string, string> Canonical = new(StringComparer.Ordinal)
    {
        ["void"] = "void", ["bool"] = "bool", ["char"] = "char",
        ["int8"] = "int8", ["uint8"] = "uint8", ["unsigned int8"] = "uint8",
        ["int16"] = "int16", ["uint16"] = "uint16", ["unsigned int16"] = "uint16",
        ["int32"] = "int32", ["uint32"] = "uint32", ["unsigned int32"] = "uint32",
        ["int64"] = "int64", ["uint64"] = "uint64", ["unsigned int64"] = "uint64",
        ["float32"] = "float32", ["float64"] = "float64",
        ["string"] = "string", ["object"] = "object",
        ["native int"] = "native int", ["nativeint"] = "native int",
        ["native uint"] = "native uint", ["nativeuint"] = "native uint", ["native unsigned int"] = "native uint",
        ["typedref"] = "typedref",
        // C# spellings are accepted too; they are handy at a prompt.
        ["int"] = "int32", ["long"] = "int64", ["short"] = "int16", ["byte"] = "uint8",
        ["sbyte"] = "int8", ["uint"] = "uint32", ["ulong"] = "uint64", ["ushort"] = "uint16",
        ["float"] = "float32", ["double"] = "float64", ["nint"] = "native int", ["nuint"] = "native uint",
    };

    private static readonly Dictionary<string, string> CoreLibNames = new(StringComparer.Ordinal)
    {
        ["void"] = "System.Void", ["bool"] = "System.Boolean", ["char"] = "System.Char",
        ["int8"] = "System.SByte", ["uint8"] = "System.Byte",
        ["int16"] = "System.Int16", ["uint16"] = "System.UInt16",
        ["int32"] = "System.Int32", ["uint32"] = "System.UInt32",
        ["int64"] = "System.Int64", ["uint64"] = "System.UInt64",
        ["float32"] = "System.Single", ["float64"] = "System.Double",
        ["string"] = "System.String", ["object"] = "System.Object",
        ["native int"] = "System.IntPtr", ["native uint"] = "System.UIntPtr",
        ["typedref"] = "System.TypedReference",
    };

    private static readonly Dictionary<string, string> KeywordsByCoreLibName = CoreLibNames.ToDictionary(p => p.Value, p => p.Key, StringComparer.Ordinal);

    private static readonly Dictionary<string, Type> Types = new(StringComparer.Ordinal)
    {
        ["void"] = typeof(void), ["bool"] = typeof(bool), ["char"] = typeof(char),
        ["int8"] = typeof(sbyte), ["uint8"] = typeof(byte),
        ["int16"] = typeof(short), ["uint16"] = typeof(ushort),
        ["int32"] = typeof(int), ["uint32"] = typeof(uint),
        ["int64"] = typeof(long), ["uint64"] = typeof(ulong),
        ["float32"] = typeof(float), ["float64"] = typeof(double),
        ["string"] = typeof(string), ["object"] = typeof(object),
        ["native int"] = typeof(nint), ["native uint"] = typeof(nuint),
        ["typedref"] = typeof(TypedReference),
    };

    /// <summary>
    /// The C# alias that is accepted as a type keyword but names no CIL primitive: it binds to
    /// <c>System.Decimal</c> like any other named type.
    /// </summary>
    public const string DecimalAlias = "decimal";

    /// <summary>
    /// The canonical CIL keywords, <c>int32</c> to <c>typedref</c>.
    /// </summary>
    public static IReadOnlyCollection<string> Keywords => Types.Keys;

    /// <summary>
    /// Every spelling the prompt accepts for a primitive, the C# ones and <c>decimal</c> included.
    /// </summary>
    public static IEnumerable<string> Spellings => Canonical.Keys.Append(DecimalAlias);

    /// <summary>
    /// Finds the canonical keyword a spelling names.
    /// </summary>
    /// <param name="spelling">A keyword as written: <c>int32</c>, <c>int</c>, <c>native int</c>, <c>nativeint</c>.</param>
    /// <param name="keyword">The canonical keyword, or <see cref="DecimalAlias"/> for <c>decimal</c>.</param>
    /// <returns>True when the spelling is a primitive keyword or the decimal alias.</returns>
    public static bool TryCanonical(string spelling, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? keyword)
    {
        ArgumentNullException.ThrowIfNull(spelling);
        if (spelling == DecimalAlias)
        {
            keyword = DecimalAlias;
            return true;
        }

        return Canonical.TryGetValue(spelling, out keyword);
    }

    /// <summary>
    /// True when a word is the first of a multi-word keyword: <c>native</c> or <c>unsigned</c>.
    /// </summary>
    /// <param name="word">The word.</param>
    /// <returns>True when a following word may complete a keyword.</returns>
    public static bool StartsMultiWord(string word) => word is "native" or "unsigned";

    /// <summary>
    /// The runtime type of a canonical keyword.
    /// </summary>
    /// <param name="keyword">A canonical keyword.</param>
    /// <returns>The type.</returns>
    /// <exception cref="ArgumentException">The keyword is not canonical.</exception>
    public static Type TypeOf(string keyword)
    {
        ArgumentNullException.ThrowIfNull(keyword);
        return Types.TryGetValue(keyword, out var type) ? type : throw new ArgumentException($"'{keyword}' is not a primitive keyword", nameof(keyword));
    }

    /// <summary>
    /// The canonical keyword of a runtime type, or null when the type is not a CIL primitive.
    /// </summary>
    /// <param name="type">The type.</param>
    /// <returns>The keyword, or null.</returns>
    public static string? KeywordOf(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        foreach (var pair in Types)
        {
            if (pair.Value == type)
            {
                return pair.Key;
            }
        }

        return null;
    }

    /// <summary>
    /// The CoreLib name of a canonical keyword, <c>System.Int32</c> for <c>int32</c>.
    /// </summary>
    /// <param name="keyword">A canonical keyword.</param>
    /// <returns>The namespace-qualified name.</returns>
    public static string CoreLibNameOf(string keyword)
    {
        ArgumentNullException.ThrowIfNull(keyword);
        return CoreLibNames.TryGetValue(keyword, out var name) ? name : throw new ArgumentException($"'{keyword}' is not a primitive keyword", nameof(keyword));
    }

    /// <summary>
    /// The canonical keyword of a CoreLib type named by namespace and name, or null when the name
    /// is not a primitive's.
    /// </summary>
    /// <param name="ns">The namespace, <c>System</c>.</param>
    /// <param name="name">The type name, <c>Int32</c>.</param>
    /// <returns>The keyword, or null.</returns>
    public static string? KeywordOfCoreLibName(string ns, string name)
    {
        ArgumentNullException.ThrowIfNull(ns);
        ArgumentNullException.ThrowIfNull(name);
        return ns == "System" && KeywordsByCoreLibName.TryGetValue(ns + "." + name, out var keyword) ? keyword : null;
    }
}
