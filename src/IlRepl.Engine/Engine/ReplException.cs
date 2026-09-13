using IlRepl.Protocol;

namespace IlRepl.Engine;

/// <summary>
/// Raised when a line cannot be accepted: a parse error, an unresolved member, a stack
/// underflow, or an invalid command. The message is written for the person at the prompt.
/// </summary>
public sealed class ReplException : Exception
{
    /// <summary>
    /// Structured source findings retained when a candidate line is refused.
    /// </summary>
    public IReadOnlyList<AnalysisDiagnostic> Diagnostics { get; init; } = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="ReplException"/> class.
    /// </summary>
    public ReplException()
    {
    }

    /// <summary>
    /// Initializes a new instance with a message for the prompt.
    /// </summary>
    /// <param name="message">The message shown to the user.</param>
    public ReplException(string message) : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance with a message and the underlying cause.
    /// </summary>
    /// <param name="message">The message shown to the user.</param>
    /// <param name="innerException">The exception that caused this one.</param>
    public ReplException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
