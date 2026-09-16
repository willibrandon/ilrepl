namespace IlRepl.Protocol;

/// <summary>
/// A native dependency frozen as exact verified bytes for one isolated comparison side.
/// </summary>
/// <param name="Name">The portable library filename used by the captured native import.</param>
/// <param name="Hash">The lowercase SHA-256 hash of the captured image.</param>
/// <param name="Image">The complete native image supplied to the isolated runtime.</param>
public sealed record ComparisonNativeLibrary(string Name, string Hash, byte[] Image);
