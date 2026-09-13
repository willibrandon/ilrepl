namespace IlRepl.Protocol;

/// <summary>
/// An exception observation without invocation wrappers or user-defined formatting.
/// </summary>
/// <param name="Type">The exception type identity.</param>
/// <param name="Message">The stored exception message.</param>
/// <param name="HResult">The numeric failure code.</param>
/// <param name="Inner">The observed inner exception, or null.</param>
public sealed record ObservedException(string Type, string? Message, int HResult, ObservedException? Inner)
{
    /// <summary>
    /// The reason this exception could not be observed completely, or null for a complete observation.
    /// </summary>
    public string? Problem { get; init; }
}
