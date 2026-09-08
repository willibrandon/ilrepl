namespace IlRepl.Protocol;

/// <summary>
/// The words the tokenizer knows: every opcode with the kind of operand it takes, the directives,
/// the commands, the ILAsm keywords, and the primitive type names. The engine builds it from its
/// own tables and the host sends it with the hello, so the front-end colours with the same words
/// the engine accepts.
/// </summary>
/// <param name="Opcodes">Every opcode by name, with its operand kind.</param>
/// <param name="Directives">Every dot-word that is a directive, <c>.locals</c> and <c>.method</c> among them.</param>
/// <param name="Commands">Every dot-word that is a command, aliases included.</param>
/// <param name="Keywords">The ILAsm keywords: <c>instance</c>, <c>public</c>, <c>cil</c>, <c>managed</c>, and the rest.</param>
/// <param name="Primitives">The primitive type names: <c>int32</c>, <c>string</c>, <c>void</c>, and the rest.</param>
public sealed record CilVocabulary(
    IReadOnlyDictionary<string, CilOperandKind> Opcodes,
    IReadOnlyList<string> Directives,
    IReadOnlyList<string> Commands,
    IReadOnlyList<string> Keywords,
    IReadOnlyList<string> Primitives);
