using System.Text.Json.Serialization;
using StreamJsonRpc.Protocol;

namespace IlRepl.Protocol;

/// <summary>
/// Serializes RPC data without reflection for the Native AOT front-end.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    MaxDepth = 256,
    GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(HostHello))]
[JsonSerializable(typeof(CommonErrorData))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(HandleReply))]
[JsonSerializable(typeof(SessionDocument))]
[JsonSerializable(typeof(SessionRequest))]
[JsonSerializable(typeof(SessionReply))]
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
