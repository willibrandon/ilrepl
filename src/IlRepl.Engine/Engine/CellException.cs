namespace IlRepl.Engine;

/// <summary>
/// Raised when a compiled cell ran and threw. The original exception is available through
/// <see cref="Exception.InnerException"/>.
/// </summary>
public sealed class CellException : Exception
{
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
