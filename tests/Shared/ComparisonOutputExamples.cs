namespace IlRepl.Tests.Shared;

/// <summary>
/// Supplies real managed and direct stream writes for desktop and browser comparisons.
/// </summary>
public static class ComparisonOutputExamples
{
    /// <summary>
    /// Writes a UTF-8 character one byte at a time and a managed character to the same stream without a newline.
    /// </summary>
    /// <param name="error">Whether to write standard error instead of standard output.</param>
    /// <param name="reverse">Whether the managed write precedes the direct writes.</param>
    /// <returns>The complete selected method.</returns>
    public static string Source(bool error, bool reverse)
    {
        var open = "call class System.IO.Stream Console::OpenStandard" + (error ? "Error" : "Output") + "()\n";
        var raw = open + "ldc.i4 195\ncallvirt instance void System.IO.Stream::WriteByte(uint8)\n"
            + open + "ldc.i4 169\ncallvirt instance void System.IO.Stream::WriteByte(uint8)\n";
        var managed = error ? "call class System.IO.TextWriter Console::get_Error()\nldstr \"B\"\n"
            + "callvirt instance void System.IO.TextWriter::Write(string)\n" : "ldstr \"B\"\ncall void Console::Write(string)\n";
        return ".method public static int32 Work() cil managed {\n" + (reverse ? managed + raw : raw + managed)
            + "ldc.i4.s 42\nret\n}";
    }
}
