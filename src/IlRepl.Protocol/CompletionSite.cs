namespace IlRepl.Protocol;

/// <summary>
/// Describes the caret's syntactic completion site and the complete replacement range.
/// </summary>
/// <remarks>
/// Where the caret stands in a line and what a completion there would replace. The range is
/// decided by the syntax, not by the caret: the whole identifier under the caret, or the whole
/// member reference, however far it runs past the caret. A following comment, another argument, a
/// suffix, and an existing <c>::</c> are never inside it.
/// </remarks>
/// <param name="Kind">What is completed here.</param>
/// <param name="Owner">The opcode or directive owning the operand, such as <c>call</c> or <c>.override with</c>.</param>
/// <param name="Prefix">The text from the start of the identifier being typed to the caret; what is matched.</param>
/// <param name="ReplaceStart">The offset the accepted text replaces from.</param>
/// <param name="ReplaceLength">How many characters it replaces; may run past the caret.</param>
/// <param name="Caret">The caret offset the site was classified at.</param>
/// <param name="DeclaringTypeText">For a member site, the declaring type as written, or null.</param>
/// <param name="ReturnTypeText">For a member site, the return or field type as written, or null.</param>
/// <param name="ExplicitInstance">For a member site, whether <c>instance</c> was written.</param>
/// <param name="ArgumentIndex">The position of the parameter, type argument, or switch entry the caret is in, or -1.</param>
/// <param name="SuppliedArguments">How many type arguments precede the caret's inside an open <c>&lt;...&gt;</c>.</param>
/// <param name="GenericOwnerText">For a type argument, the definition it belongs to as written: the type, or <c>D::Name</c>.</param>
/// <param name="NextIsDoubleColon">True when <c>::</c> already follows the range.</param>
/// <param name="NextIsAngle">True when <c>&lt;</c> already follows the range.</param>
/// <param name="NextIsParen">True when <c>(</c> already follows the range.</param>
/// <param name="DeclarationComplete">True when the directive around the site has every part its parser requires.</param>
public sealed record CompletionSite(
    CompletionSiteKind Kind,
    string Owner,
    string Prefix,
    int ReplaceStart,
    int ReplaceLength,
    int Caret,
    string? DeclaringTypeText,
    string? ReturnTypeText,
    bool ExplicitInstance,
    int ArgumentIndex,
    int SuppliedArguments,
    string? GenericOwnerText,
    bool NextIsDoubleColon,
    bool NextIsAngle,
    bool NextIsParen,
    bool DeclarationComplete)
{
    /// <summary>
    /// No site.
    /// </summary>
    public static CompletionSite None { get; } = new(CompletionSiteKind.None, "", "", 0, 0, 0, null, null, false, -1, 0, null, false, false,
        false, true);

    /// <summary>
    /// True when the caret is in an operand something can be listed for.
    /// </summary>
    public bool IsOperand => Kind != CompletionSiteKind.None;

    /// <summary>
    /// Whether the caret names a function pointer's return type, even inside an outer parameter declaration.
    /// </summary>
    public bool IsFunctionPointerReturn { get; init; }

    /// <summary>
    /// The exclusive end of the candidate generic name, or -1 when no name is identified.
    /// </summary>
    public int GenericNameEnd { get; init; } = -1;

    /// <summary>
    /// The actual opening angle bracket offset, including any intervening whitespace or comments, or -1.
    /// </summary>
    public int GenericOpenOffset { get; init; } = -1;

    /// <summary>
    /// The offset just past the range.
    /// </summary>
    public int ReplaceEnd => ReplaceStart + ReplaceLength;
}
