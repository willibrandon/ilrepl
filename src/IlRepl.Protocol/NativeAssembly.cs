namespace IlRepl.Protocol;

/// <summary>
/// An immutable assembly image used by an inspection worker.
/// </summary>
public sealed record NativeAssembly
{
    /// <summary>
    /// The full assembly identity.
    /// </summary>
    public string Name { get; init; } = "";

    /// <summary>
    /// The original PE image.
    /// </summary>
    public byte[] Image { get; init; } = [];

    /// <summary>
    /// The source role: reference, methods, types, trampoline, or helper.
    /// </summary>
    public string Role { get; init; } = "reference";
}
