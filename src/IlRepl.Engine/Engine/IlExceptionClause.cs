namespace IlRepl.Engine;

/// <summary>
/// One exception handling clause of a method body, normalized to offsets: the protected range,
/// the filter code for a filter clause, the handler, and the catch type as far as it resolved.
/// Kept on a disassembled method whether or not the listing draws it as a block, because the
/// stack analysis and the fallback rendering both read it.
/// </summary>
/// <param name="Kind">The clause kind.</param>
/// <param name="TryStart">The first offset of the protected range.</param>
/// <param name="TryEnd">The offset just past the protected range.</param>
/// <param name="FilterStart">The first offset of the filter code, for a filter clause; null otherwise.</param>
/// <param name="HandlerStart">The first offset of the handler.</param>
/// <param name="HandlerEnd">The offset just past the handler.</param>
/// <param name="CatchToken">The catch type token, or 0.</param>
/// <param name="CatchSignature">The catch type as the metadata spells it, or null.</param>
/// <param name="CatchType">The catch type as a runtime type, or null when it did not resolve or the clause has none.</param>
public sealed record IlExceptionClause(
    IlClauseKind Kind,
    int TryStart,
    int TryEnd,
    int? FilterStart,
    int HandlerStart,
    int HandlerEnd,
    int CatchToken,
    IlSignature? CatchSignature,
    Type? CatchType)
{
    /// <summary>
    /// Where the clause's code begins lexically: the filter for a filter clause, the handler otherwise.
    /// </summary>
    public int LexicalStart => FilterStart ?? HandlerStart;

    /// <summary>
    /// The clause in ildasm's offset form, for a layout braces cannot draw.
    /// </summary>
    /// <returns>For example <c>.try IL_0000 to IL_000a catch [System.Runtime]System.Exception handler IL_000a to IL_0014</c>.</returns>
    public string Describe()
    {
        var head = $".try {IlReader.LabelFor(TryStart)} to {IlReader.LabelFor(TryEnd)} ";
        var handler = $"handler {IlReader.LabelFor(HandlerStart)} to {IlReader.LabelFor(HandlerEnd)}";
        return Kind switch
        {
            IlClauseKind.Catch => head + "catch " + (CatchSignature is null ? (CatchType is null ? $"0x{CatchToken:x8}" : TypeNameFormatter.IlAsmDeclaring(CatchType)) : CatchText()) + " " + handler,
            IlClauseKind.Filter => head + "filter " + IlReader.LabelFor(FilterStart ?? HandlerStart) + " " + handler,
            IlClauseKind.Finally => head + "finally " + handler,
            _ => head + "fault " + handler,
        };
    }

    private string CatchText()
    {
        var text = IlSignatureRenderer.IlAsm(CatchSignature!);
        return text.StartsWith("class ", StringComparison.Ordinal) ? text[6..] : text.StartsWith("valuetype ", StringComparison.Ordinal) ? text[10..] : text;
    }
}
