using System.Globalization;

namespace IlRepl.Engine;

/// <summary>
/// Renders constants the way ILAsm writes them: <c>int32(5)</c>, <c>"text"</c>, <c>bool(true)</c>.
/// </summary>
public static class ConstantText
{
    /// <summary>
    /// Renders a constant in ILAsm's typed form.
    /// </summary>
    /// <param name="value">The constant, or null for <c>nullref</c>.</param>
    /// <returns>The ILAsm text.</returns>
    public static string Describe(object? value) => value switch
    {
        null => "nullref",
        string s => LiteralParser.Escape(s),
        bool b => "bool(" + (b ? "true" : "false") + ")",
        char c => "char(" + ((int)c).ToString(CultureInfo.InvariantCulture) + ")",
        sbyte v => "int8(" + v.ToString(CultureInfo.InvariantCulture) + ")",
        byte v => "uint8(" + v.ToString(CultureInfo.InvariantCulture) + ")",
        short v => "int16(" + v.ToString(CultureInfo.InvariantCulture) + ")",
        ushort v => "uint16(" + v.ToString(CultureInfo.InvariantCulture) + ")",
        int v => "int32(" + v.ToString(CultureInfo.InvariantCulture) + ")",
        uint v => "uint32(" + v.ToString(CultureInfo.InvariantCulture) + ")",
        long v => "int64(" + v.ToString(CultureInfo.InvariantCulture) + ")",
        ulong v => "uint64(" + v.ToString(CultureInfo.InvariantCulture) + ")",
        float v => "float32(" + v.ToString("R", CultureInfo.InvariantCulture) + ")",
        double v => "float64(" + v.ToString("R", CultureInfo.InvariantCulture) + ")",
        Type t => "type(" + TypeNameFormatter.Pretty(t) + ")",
        Enum e => TypeNameFormatter.Pretty(e.GetType()) + "." + e.ToString(),
        Array a => "{" + string.Join(", ", a.Cast<object?>().Select(Describe)) + "}",
        _ => value.ToString() ?? "",
    };
}
