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

    /// <summary>
    /// Builds a constructor whose finally handler either preserves or replaces argument zero.
    /// </summary>
    /// <param name="originalReceiver">Whether the handler stores the original receiver.</param>
    /// <returns>The complete class declaration.</returns>
    public static string[] FinallySource(bool originalReceiver) =>
    [
        ".class public FlowFinallyArgument {",
        ".field public initonly int32 Value",
        ".method public instance void .ctor(class FlowFinallyArgument other) {",
        "ldarg.0",
        "call instance void object::.ctor()",
        ".try {",
        "leave DONE",
        "} finally {",
        originalReceiver ? "ldarg.0" : "ldarg.1",
        "starg.s 0",
        "}",
        "DONE: ldarg.0",
        "ldc.i4.s 42",
        "stfld int32 FlowFinallyArgument::Value",
        "ret",
        "}",
        "}",
    ];

    /// <summary>
    /// Builds a constructor whose accepting filter preserves or replaces argument zero before its handler uses it.
    /// </summary>
    /// <param name="originalReceiver">Whether the filter stores the original receiver.</param>
    /// <returns>The complete class declaration.</returns>
    public static string[] FilterSource(bool originalReceiver) =>
    [
        ".class public FlowFilterArgument {",
        ".field public initonly int32 Value",
        ".method public instance void .ctor(class FlowFilterArgument other) {",
        "ldarg.0",
        "call instance void object::.ctor()",
        ".try {",
        "ldnull",
        "throw",
        "} filter {",
        "pop",
        originalReceiver ? "ldarg.0" : "ldarg.1",
        "starg.s 0",
        "ldc.i4.1",
        "endfilter",
        "} handler {",
        "pop",
        "ldarg.0",
        "ldc.i4.s 42",
        "stfld int32 FlowFilterArgument::Value",
        "leave DONE",
        "}",
        "DONE: ret",
        "}",
        "}",
    ];

    /// <summary>
    /// Builds a constructor whose inner finally completes while its outer finally cannot complete.
    /// </summary>
    /// <returns>The complete class declaration.</returns>
    public static string[] NestedNonCompletingFinallySource() =>
    [
        ".class public NestedFinallyArgument {",
        ".field public initonly int32 Value",
        ".method public instance void .ctor(class NestedFinallyArgument other) {",
        "ldarg.0",
        "call instance void object::.ctor()",
        ".try {",
        "leave DONE",
        "} finally {",
        ".try {",
        "nop",
        "} finally {",
        "ldarg.1",
        "starg.s 0",
        "endfinally",
        "}",
        "OUTERLOOP: br OUTERLOOP",
        "}",
        "DONE: ldarg.0",
        "ldc.i4.1",
        "stfld int32 NestedFinallyArgument::Value",
        "ret",
        "}",
        "}",
    ];

    /// <summary>
    /// Builds a constructor that writes through the address of argument zero before loading it again.
    /// </summary>
    public static string[] AddressSource(string write) =>
    [
        ".class public FlowAddressArgument {",
        ".field public initonly int32 Value",
        ".method public instance void .ctor(class FlowAddressArgument other) {",
        "ldarg.0",
        "call instance void object::.ctor()",
        "ldarga.s 0",
        .. (write switch
        {
            "stind.ref" => new[] { "ldarg.1", "stind.ref" },
            "stobj" => ["ldarg.1", "stobj class FlowAddressArgument"],
            "initobj" => ["initobj class FlowAddressArgument"],
            "cpobj" => ["ldarga.s 1", "cpobj class FlowAddressArgument"],
            "initblk" => ["ldc.i4.0", "ldc.i4.8", "initblk"],
            "cpblk" => ["ldarga.s 1", "ldc.i4.8", "cpblk"],
            _ => throw new ArgumentOutOfRangeException(nameof(write)),
        }),
        "ldarg.0",
        "ldc.i4.s 42",
        "stfld int32 FlowAddressArgument::Value",
        "ret",
        "}",
        "}",
    ];
}
