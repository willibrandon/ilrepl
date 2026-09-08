namespace IlRepl.Repl;

/// <summary>
/// The dot-words the prompt takes as directives rather than commands. They live apart from
/// <see cref="ReplCore"/> so the tokenizer's vocabulary can read them without touching the
/// core's own initialization.
/// </summary>
public static class ReplDirectives
{
    /// <summary>
    /// The directives, in the order the REPL learned them.
    /// </summary>
    public static IReadOnlyList<string> Names { get; } =
    [
        ".locals", ".args", ".typeparams", ".typeargs", ".vararg", ".method", ".try", ".maxstack",
        ".class", ".field", ".property", ".event", ".get", ".set", ".other", ".addon", ".removeon", ".fire", ".override", ".pack", ".size", ".param", ".custom",
    ];
}
