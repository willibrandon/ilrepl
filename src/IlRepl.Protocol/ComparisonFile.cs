namespace IlRepl.Protocol;

/// <summary>
/// A file, directory, or symbolic link copied into each comparison runtime's separate working directory.
/// </summary>
/// <param name="Path">The relative path beneath the working directory.</param>
/// <param name="Contents">The immutable contents supplied to both sides.</param>
public sealed record ComparisonFile(string Path, byte[] Contents)
{
    /// <summary>
    /// Whether this entry represents a directory, including an empty directory or a directory link.
    /// </summary>
    public bool IsDirectory { get; init; }

    /// <summary>
    /// The relative destination of a symbolic link within the captured fixture tree, or null for an ordinary entry.
    /// </summary>
    public string? LinkTarget { get; init; }

    /// <summary>
    /// The captured creation time in UTC, or null when no timestamp was supplied.
    /// </summary>
    public DateTime? CreationTimeUtc { get; init; }

    /// <summary>
    /// The captured last-write time in UTC, or null when no timestamp was supplied.
    /// </summary>
    public DateTime? LastWriteTimeUtc { get; init; }

    /// <summary>
    /// The captured last-access time in UTC, or null when no timestamp was supplied.
    /// </summary>
    public DateTime? LastAccessTimeUtc { get; init; }
}
