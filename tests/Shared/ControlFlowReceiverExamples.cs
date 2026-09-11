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

    /// <summary>
    /// Builds a constructor that may replace argument zero before loading it for a readonly field store.
    /// </summary>
    public static string[] ArgumentSource(bool originalReceiver, bool branch) =>
    [
        ".class public FlowArgument {",
        ".field public initonly int32 Value",
        ".method public instance void .ctor(class FlowArgument other, bool replace) {",
        "ldarg.0",
        "call instance void object::.ctor()",
        .. (branch ? new[] { "ldarg.2", "brfalse STORE" } : []),
        originalReceiver ? "ldarg.0" : "ldarg.1",
        "starg.s 0",
        "STORE: ldarg.0",
        "ldc.i4.s 42",
        "stfld int32 FlowArgument::Value",
        "ret",
        "}",
        "}",
    ];

    /// <summary>
    /// Builds a handler reached after argument zero may have lost its original receiver.
    /// </summary>
    public static string[] HandlerSource(bool originalReceiver) =>
    [
        ".class public FlowHandlerArgument {",
        ".field public initonly int32 Value",
        ".method public instance void .ctor(class FlowHandlerArgument other) {",
        "ldarg.0",
        "call instance void object::.ctor()",
        originalReceiver ? "ldarg.0" : "ldarg.1",
        "starg.s 0",
        ".try {",
        "ldnull",
        "throw",
        "} catch object {",
        "pop",
        "ldarg.0",
        "ldc.i4.s 42",
        "stfld int32 FlowHandlerArgument::Value",
        "leave DONE",
        "}",
        "DONE: ret",
        "}",
        "}",
    ];
}
