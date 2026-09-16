namespace IlRepl.Protocol;

/// <summary>
/// Explains an instruction without retaining runtime types or requiring an online reference.
/// </summary>
/// <param name="Mnemonic">The canonical opcode name.</param>
/// <param name="Syntax">The instruction syntax or resolved operand signature.</param>
/// <param name="StackEffect">The values consumed and produced, bottom first.</param>
/// <param name="Explanation">The immediate explanation of the instruction.</param>
/// <param name="Notes">Relevant distinctions and requirements.</param>
/// <param name="DocumentationUrl">The fuller opcode reference.</param>
public sealed record InstructionHelp(string Mnemonic, string Syntax, string StackEffect, string Explanation,
    IReadOnlyList<string> Notes, string DocumentationUrl);
