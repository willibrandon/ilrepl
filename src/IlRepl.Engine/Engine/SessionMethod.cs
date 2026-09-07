namespace IlRepl.Engine;

/// <summary>
/// A method defined with <c>.method</c> and kept by the session. The body is stored as the lines
/// the user typed, so it can be replayed, and as the validated state those lines produced, which
/// the compiler and the renderer read directly.
/// </summary>
/// <param name="Signature">The signature.</param>
/// <param name="HeaderLine">The <c>.method</c> line as typed.</param>
/// <param name="BodyLines">The body lines as typed, without the closing brace.</param>
/// <param name="State">The validated body, built against the method table in force when it was committed.</param>
public sealed record SessionMethod(MethodSignature Signature, string HeaderLine, IReadOnlyList<string> BodyLines, CellState State);
