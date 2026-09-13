using System.Text.Json.Serialization;
using StreamJsonRpc.Protocol;

namespace IlRepl.Protocol;

/// <summary>
/// Source-generated JSON serialization for every type that crosses the RPC boundary, so the
/// Native AOT front-end needs no reflection.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(HostHello))]
[JsonSerializable(typeof(CommonErrorData))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(HandleReply))]
[JsonSerializable(typeof(ComparisonPackage))]
[JsonSerializable(typeof(ComparisonSide))]
[JsonSerializable(typeof(InvocationObservation))]
[JsonSerializable(typeof(ObservedValue))]
[JsonSerializable(typeof(TranscriptLine))]
[JsonSerializable(typeof(TranscriptSpan))]
[JsonSerializable(typeof(CompletionItem))]
[JsonSerializable(typeof(CompletionRequest))]
[JsonSerializable(typeof(CompletionReply))]
[JsonSerializable(typeof(AnalysisRequest))]
[JsonSerializable(typeof(AnalysisReply))]
[JsonSerializable(typeof(ContinuationAnchor))]
[JsonSerializable(typeof(SessionStatus))]
[JsonSerializable(typeof(SessionMark))]
[JsonSerializable(typeof(CilVocabulary))]
[JsonSerializable(typeof(CilOperandKind))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(int))]
public sealed partial class ProtocolJsonContext : JsonSerializerContext;
