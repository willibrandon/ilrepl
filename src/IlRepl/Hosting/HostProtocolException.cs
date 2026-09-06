using IlRepl.Protocol;

namespace IlRepl.Hosting;

/// <summary>
/// Raised when the host process cannot be started or stops answering.
/// </summary>
public sealed class HostProtocolException : ReplEngineException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="HostProtocolException"/> class.
    /// </summary>
    public HostProtocolException()
    {
    }

    /// <summary>
    /// Initializes a new instance with a message.
    /// </summary>
    /// <param name="message">What went wrong.</param>
    public HostProtocolException(string message) : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance with a message and a cause.
    /// </summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">The underlying exception.</param>
    public HostProtocolException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
