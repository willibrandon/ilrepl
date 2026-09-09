using System.Text.Json.Serialization;

namespace IlRepl.Protocol;

/// <summary>
/// One completion candidate for a word or an operand.
/// </summary>
/// <param name="Name">The opcode or command name.</param>
/// <param name="Detail">A short detail column, such as the stack transition.</param>
/// <param name="Description">A one-line description.</param>
/// <param name="TakesOperand">True when the completed word is followed by an operand, so a space is appended.</param>
public sealed record CompletionItem(string Name, string Detail, string Description, bool TakesOperand)
{
    /// <summary>
    /// The insertion spelling, or null to insert the label.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Insert { get; init; }

    /// <summary>
    /// The operand source; none for the first-word catalog.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public CompletionKind Kind { get; init; }

    /// <summary>
    /// Whether accepting this row continues completion at the next component.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Continues { get; init; }

    /// <summary>
    /// An optional caret offset from the insertion start, including preserved continuation punctuation.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CaretOffset { get; init; }

    /// <summary>
    /// The complete signature for the detail pane.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FullDetail { get; init; }

    /// <summary>
    /// The host token for a selected generic definition.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Continuation { get; init; }

    /// <summary>
    /// The generic owner a type argument serves.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Owner { get; init; }

    /// <summary>
    /// The text inserted when accepting this item.
    /// </summary>
    [JsonIgnore]
    public string InsertText => Insert ?? Name;
}
