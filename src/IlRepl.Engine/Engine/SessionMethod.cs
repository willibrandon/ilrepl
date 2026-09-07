namespace IlRepl.Engine;

/// <summary>
/// A method defined with <c>.method</c> and kept by the session. The body is stored as the lines
/// the user typed, so it can be shown and exported, and as the validated state those lines
/// produced, which is what was compiled: nothing is parsed again after a method is accepted. The
/// trampoline is the identity callers bind to; the version is the body it forwards to now.
/// </summary>
/// <param name="Signature">The signature.</param>
/// <param name="HeaderLine">The <c>.method</c> line as typed.</param>
/// <param name="BodyLines">The body lines as typed, without the closing brace.</param>
/// <param name="State">The validated body, built against the method table in force when it was committed.</param>
/// <param name="Trampoline">The stable entry point callers bind to.</param>
/// <param name="Version">The compiled body the trampoline forwards to.</param>
public sealed record SessionMethod(MethodSignature Signature, string HeaderLine, IReadOnlyList<string> BodyLines, CellState State, MethodTrampoline Trampoline, CompiledMethodVersion Version)
{
    /// <summary>
    /// The submission that accepted this definition, which orders a rebuild after its dependencies.
    /// </summary>
    public int Order { get; init; }
}
