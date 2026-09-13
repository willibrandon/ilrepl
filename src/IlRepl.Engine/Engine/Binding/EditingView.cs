using IlRepl.Protocol;

namespace IlRepl.Engine.Binding;

/// <summary>
/// The symbols and body context at the caret, valid while its editing session owns the metadata lease.
/// </summary>
/// <param name="Snapshot">The context's captured metadata and editing declarations.</param>
/// <param name="Scope">The binder scope at the caret.</param>
/// <param name="Owner">The open type, or null.</param>
/// <param name="OpenMethod">The open method, or null.</param>
/// <param name="Stack">The evaluation stack, bottom first.</param>
/// <param name="PrecedingInstruction">The preceding instruction, for prefixes.</param>
/// <param name="DefinedLabels">The body's defined labels.</param>
/// <param name="PendingLabels">The body's unresolved branch targets.</param>
/// <param name="LabelSpace">The body's label-space identity.</param>
/// <param name="InBlockComment">The lexical state before the caret's line.</param>
/// <param name="SkippedLines">Refused prefix lines, isolated from this context.</param>
public sealed record EditingView(
    BindingSnapshot Snapshot,
    IBindingScope Scope,
    TypeSymbol? Owner,
    MethodSymbol? OpenMethod,
    IReadOnlyList<TypeSymbol?> Stack,
    BoundInstruction? PrecedingInstruction,
    IReadOnlySet<string> DefinedLabels,
    IReadOnlySet<string> PendingLabels,
    long LabelSpace,
    bool InBlockComment,
    IReadOnlyList<SkippedEditingLine> SkippedLines)
{
    /// <summary>
    /// Whether the complete incoming stack is known at the caret.
    /// </summary>
    public AnalyzedStackKind StackKind { get; init; } = AnalyzedStackKind.Known;

    /// <summary>
    /// The complete declaration context used to validate selected definitions across symbolic replay.
    /// </summary>
    internal string DeclarationContext { get; init; } = "";

    /// <summary>
    /// Whether an unsubmitted command prevents binding source after it against this snapshot.
    /// </summary>
    internal bool BindingRefreshRequired { get; init; }

    /// <summary>
    /// The concrete arguments assigned to the cell's generic parameters, or null before an assignment.
    /// </summary>
    public IReadOnlyList<TypeSymbol>? TypeArguments { get; init; }

    /// <summary>
    /// Whether the cell or open method uses the vararg calling convention.
    /// </summary>
    public bool IsVarArg { get; init; }

    /// <summary>
    /// The original receiver provenance of each stack slot, ordered from bottom to top.
    /// </summary>
    public IReadOnlyList<bool> ThisSlots { get; init; } = [];

    /// <summary>
    /// The kind of the open class, struct, interface or enum, or null at cell level.
    /// </summary>
    public TypeKind? OwnerKind { get; init; }

    /// <summary>
    /// Whether a stack slot still holds the method's original receiver.
    /// </summary>
    /// <param name="index">The bottom-based stack index.</param>
    /// <returns>Whether the slot is the original receiver.</returns>
    public bool IsThisAt(int index) => index >= 0 && index < ThisSlots.Count && ThisSlots[index];
}
