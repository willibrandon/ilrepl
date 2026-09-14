namespace IlRepl.Tests.Engine;

/// <summary>
/// Exposes stored state while every virtual formatting getter fails if an observer invokes it.
/// </summary>
internal sealed class StoredFieldException(string detail) : Exception("stored message")
{
    /// <summary>
    /// The caller-visible state that a structural observer must preserve.
    /// </summary>
    internal readonly string Detail = detail;

    /// <summary>
    /// Rejects virtual message access during observation.
    /// </summary>
    public override string Message => throw new InvalidOperationException("Message getter executed");

    /// <summary>
    /// Rejects virtual stack formatting during observation.
    /// </summary>
    public override string? StackTrace => throw new InvalidOperationException("StackTrace getter executed");

    /// <summary>
    /// Rejects user formatting during observation.
    /// </summary>
    /// <returns>No formatted value is returned.</returns>
    public override string ToString() => throw new InvalidOperationException("ToString executed");
}
