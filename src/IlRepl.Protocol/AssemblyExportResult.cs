namespace IlRepl.Protocol;

/// <summary>
/// Carries an exported assembly to a frontend that provides its own file download destination.
/// </summary>
public sealed record AssemblyExportResult
{
    /// <summary>
    /// The destination name requested by the user.
    /// </summary>
    public required string Path { get; init; }

    /// <summary>
    /// The complete managed portable executable image.
    /// </summary>
    public required byte[] Image { get; init; }
}
