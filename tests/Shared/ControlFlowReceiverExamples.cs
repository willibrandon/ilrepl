namespace IlRepl.Tests.Shared;

/// <summary>
/// Supplies constructor branches whose later edge changes the receiver of an earlier readonly field store.
/// </summary>
public static class ControlFlowReceiverExamples
{
    /// <summary>
    /// Builds a complete class whose constructor either preserves this on every path or merges another receiver.
    /// </summary>
    public static string[] Source(bool originalReceiver) =>
    [
        ".class public FlowReceiver {",
        ".field public initonly int32 Value",
        ".method public instance void .ctor(class FlowReceiver other) {",
        "ldarg.0",
        "call instance void object::.ctor()",
        "ldarg.1",
        "brtrue LATER",
        "ldarg.0",
        "br STORE",
        "STORE: ldc.i4.s 42",
        "stfld int32 FlowReceiver::Value",
        "br DONE",
        originalReceiver ? "LATER: ldarg.0" : "LATER: ldarg.1",
        "br STORE",
        "DONE: ret",
        "}",
        "}",
    ];
}
