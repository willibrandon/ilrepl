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
[JsonSerializable(typeof(ExecutionProgress))]
[JsonSerializable(typeof(ExecutionOutput))]
[JsonSerializable(typeof(SessionCheckpointRevision))]
[JsonSerializable(typeof(CommonErrorData))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(HandleReply))]
[JsonSerializable(typeof(HandleReply[]))]
[JsonSerializable(typeof(AnalysisLocation[]))]
[JsonSerializable(typeof(AssemblyExportResult))]
[JsonSerializable(typeof(OwnedProcessScope))]
[JsonSerializable(typeof(SupervisorLaunch))]
[JsonSerializable(typeof(SupervisorSnapshot))]
[JsonSerializable(typeof(SessionDocument))]
[JsonSerializable(typeof(SessionRequest))]
[JsonSerializable(typeof(SessionReply))]
[JsonSerializable(typeof(ComparisonPackage))]
[JsonSerializable(typeof(NativePackage))]
[JsonSerializable(typeof(NativeWorkerState))]
[JsonSerializable(typeof(NativeReply))]
[JsonSerializable(typeof(NativeReport))]
[JsonSerializable(typeof(NativeTarget))]
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
[JsonSerializable(typeof(AnalysisPosition))]
[JsonSerializable(typeof(ContinuationAnchor))]
[JsonSerializable(typeof(SessionStatus))]
[JsonSerializable(typeof(SessionMark))]
[JsonSerializable(typeof(CilVocabulary))]
[JsonSerializable(typeof(CilOperandKind))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(int))]
public sealed partial class ProtocolJsonContext : JsonSerializerContext;
