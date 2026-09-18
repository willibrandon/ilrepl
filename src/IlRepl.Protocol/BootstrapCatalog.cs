using System.Text.Json;

namespace IlRepl.Protocol;

/// <summary>
/// Supplies generated engine vocabulary and initial status before the execution host finishes starting.
/// </summary>
public static partial class BootstrapCatalog
{
    /// <summary>
    /// The AOT-compatible initial catalog generated from the engine's authoritative opcode and command tables.
    /// </summary>
    public static HostHello Hello { get; } = JsonSerializer.Deserialize(Data, ProtocolJsonContext.Default.HostHello)
        ?? throw new InvalidDataException("The embedded ilrepl bootstrap catalog is invalid.");
}
