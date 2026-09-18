using System.Text.Json.Serialization;

namespace IlRepl.Tests.Responsiveness;

/// <summary>
/// Provides generated metadata for responsiveness artifacts and committed baseline records.
/// </summary>
[JsonSerializable(typeof(ResponsivenessRecord))]
[JsonSerializable(typeof(ResponsivenessRecord[]))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal sealed partial class ResponsivenessJsonContext : JsonSerializerContext;
