namespace IlRepl.Protocol;

/// <summary>
/// Raised when an engine cannot take a line at all, as opposed to a line that produced an error
/// in the transcript. The host client raises it when the host process cannot be reached.
/// </summary>
public class ReplEngineException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ReplEngineException"/> class.
    /// </summary>
    public ReplEngineException()
    {
    }

    /// <summary>
    /// Initializes a new instance with a message.
    /// </summary>
    /// <param name="message">What went wrong.</param>
    public ReplEngineException(string message) : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance with a message and a cause.
    /// </summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">The underlying exception.</param>
    public ReplEngineException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
