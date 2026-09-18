using System.Text.Json.Serialization;

namespace IlRepl.Protocol;

/// <summary>
/// Serializes the private measurement artifact on Native AOT and JIT runtimes.
/// </summary>
[JsonSerializable(typeof(ProcessMeasurement))]
public sealed partial class MeasurementJsonContext : JsonSerializerContext;
