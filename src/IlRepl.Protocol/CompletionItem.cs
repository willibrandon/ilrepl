namespace IlRepl.Protocol;

/// <summary>
/// One completion candidate for the first word of a line.
/// </summary>
/// <param name="Name">The opcode or command name.</param>
/// <param name="Detail">A short detail column, such as the stack transition.</param>
/// <param name="Description">A one-line description.</param>
/// <param name="TakesOperand">True when the completed word is followed by an operand, so a space is appended.</param>
public sealed record CompletionItem(string Name, string Detail, string Description, bool TakesOperand);
