using System.Text.Json.Serialization;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Defines the serialized contracts exchanged with isolated export probes.
/// </summary>
[JsonSerializable(typeof(ExportRequest))]
[JsonSerializable(typeof(ExportObservation))]
internal sealed partial class ExportJsonContext : JsonSerializerContext;
