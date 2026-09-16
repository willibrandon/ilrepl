namespace IlRepl.Engine;

/// <summary>
/// Retains a cell's original exception and the output produced before execution failed.
/// </summary>
public sealed class CellException : Exception
{
    /// <summary>
    /// The standard output captured before the cell threw.
    /// </summary>
    public string StandardOutput { get; internal set; } = "";

    /// <summary>
    /// The standard error captured before the cell threw.
    /// </summary>
    public string StandardError { get; internal set; } = "";

    /// <summary>
    /// Initializes a new instance of the <see cref="CellException"/> class.
    /// </summary>
    public CellException()
    {
    }

    /// <summary>
    /// Initializes a new instance with a message.
    /// </summary>
    /// <param name="message">The message describing the failure.</param>
    public CellException(string message) : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance that wraps the exception thrown by the cell.
    /// </summary>
    /// <param name="message">The message describing the failure.</param>
    /// <param name="innerException">The exception the cell threw.</param>
    public CellException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
