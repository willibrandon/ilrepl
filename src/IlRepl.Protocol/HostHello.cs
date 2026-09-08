namespace IlRepl.Protocol;

/// <summary>
/// What the host reports when a session opens: the completion catalog and the initial status.
/// </summary>
/// <param name="Catalog">Every opcode and command the completer offers.</param>
/// <param name="Vocabulary">The words the tokenizer colours with.</param>
/// <param name="Status">The status of the fresh session.</param>
public sealed record HostHello(IReadOnlyList<CompletionItem> Catalog, CilVocabulary Vocabulary, SessionStatus Status);
