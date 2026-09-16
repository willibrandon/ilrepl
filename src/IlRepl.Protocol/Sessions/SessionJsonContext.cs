using System.Text.Json.Serialization;

namespace IlRepl.Protocol;

/// <summary>
/// Serializes the portable session format without reflection on desktop and WebAssembly.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, UseStringEnumConverter = true, MaxDepth = 64)]
[JsonSerializable(typeof(SessionDocument))]
public sealed partial class SessionJsonContext : JsonSerializerContext;
