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
    /// Builds a filter whose receiver-changing path rejects the exception before the handler.
    /// </summary>
    /// <param name="zero">The instruction that produces the rejecting result.</param>
    /// <param name="one">The instruction that produces the accepting result.</param>
    /// <param name="invalidateAfterMerge">Whether every result path changes the receiver before returning.</param>
    /// <param name="resultConversion">An optional conversion applied after the result paths merge.</param>
    /// <returns>The complete class declaration.</returns>
    public static string[] SelectiveFilterSource(string zero = "ldc.i4.0", string one = "ldc.i4.1",
        bool invalidateAfterMerge = false, string? resultConversion = null) =>
    [
        ".class public SelectiveFilterArgument {",
        ".field public initonly int32 Value",
        ".method public instance void .ctor(class SelectiveFilterArgument other, bool accept) {",
        "ldarg.0",
        "call instance void object::.ctor()",
        ".try {",
        "ldnull",
        "throw",
        "} filter {",
        "pop",
        "ldarg.2",
        "brtrue ACCEPT",
        "ldarg.1",
        "starg.s 0",
        zero,
        "br RESULT",
        $"ACCEPT: {one}",
        .. (invalidateAfterMerge ? new[] { "RESULT: ldarg.1", "starg.s 0", "endfilter" }
            : resultConversion is not null ? [$"RESULT: {resultConversion}", "endfilter"] : ["RESULT: endfilter"]),
        "} handler {",
        "pop",
        "ldarg.0",
        "ldc.i4.s 42",
        "stfld int32 SelectiveFilterArgument::Value",
        "leave DONE",
        "}",
        "DONE: ret",
        "}",
        "}",
    ];

    /// <summary>
    /// Builds a filter that carries its correlated decision through a local slot.
    /// </summary>
    /// <param name="overwriteThroughAddress">Whether a writable local address replaces both decisions with one.</param>
    /// <returns>The complete class declaration.</returns>
    public static string[] LocalFilterSource(bool overwriteThroughAddress = false) =>
    [
        ".class public LocalFilterArgument {",
        ".field public initonly int32 Value",
        ".method public instance void .ctor(class LocalFilterArgument other, bool accept) {",
        ".locals init (int32 decision)",
        "ldarg.0",
        "call instance void object::.ctor()",
        ".try {",
        "ldnull",
        "throw",
        "} filter {",
        "pop",
        "ldarg.2",
        "brtrue ACCEPT",
        "ldarg.1",
        "starg.s 0",
        "ldc.i4.0",
        "stloc.0",
        "br RESULT",
        "ACCEPT: ldc.i4.1",
        "stloc.0",
        .. (overwriteThroughAddress
            ? new[] { "RESULT: ldloca.s 0", "ldc.i4.1", "stind.i4", "ldloc.0" }
            : ["RESULT: ldloc.0"]),
        "endfilter",
        "} handler {",
        "pop",
        "ldarg.0",
        "ldc.i4.s 42",
        "stfld int32 LocalFilterArgument::Value",
        "leave DONE",
        "}",
        "DONE: ret",
        "}",
        "}",
    ];

    /// <summary>
    /// Builds a filter that reads a decision assigned in its protected region.
    /// </summary>
    /// <returns>The complete class declaration.</returns>
    public static string[] ProtectedLocalFilterSource() =>
    [
        ".class public ProtectedLocalFilterArgument {",
        ".field public initonly int32 Value",
        ".method public instance void .ctor(class ProtectedLocalFilterArgument other, bool accept) {",
        ".locals init (int32 decision)",
        "ldarg.0",
        "call instance void object::.ctor()",
        ".try {",
        "ldarg.2",
        "brtrue ACCEPT",
        "ldc.i4.0",
        "stloc.0",
        "ldarg.1",
        "starg.s 0",
        "br THROW",
        "ACCEPT: ldc.i4.1",
        "stloc.0",
        "THROW: ldnull",
        "throw",
        "} filter {",
        "pop",
        "ldloc.0",
        "endfilter",
        "} handler {",
        "pop",
        "ldarg.0",
        "ldc.i4.s 42",
        "stfld int32 ProtectedLocalFilterArgument::Value",
        "leave DONE",
        "}",
        "DONE: ret",
        "}",
        "}",
    ];

    /// <summary>
    /// Builds a filter that carries its correlated decision through an argument slot.
    /// </summary>
    /// <param name="overwriteThroughAddress">Whether a writable argument address replaces both decisions with one.</param>
    /// <returns>The complete class declaration.</returns>
    public static string[] ArgumentFilterSource(bool overwriteThroughAddress = false) =>
    [
        ".class public ArgumentFilterArgument {",
        ".field public initonly int32 Value",
        ".method public instance void .ctor(class ArgumentFilterArgument other, int32 decision) {",
        "ldarg.0",
        "call instance void object::.ctor()",
        ".try {",
        "ldnull",
        "throw",
        "} filter {",
        "pop",
        "ldarg.2",
        "brtrue ACCEPT",
        "ldarg.1",
        "starg.s 0",
        "ldc.i4.0",
        "starg.s 2",
        "br RESULT",
        "ACCEPT: ldc.i4.1",
        "starg.s 2",
        .. (overwriteThroughAddress
            ? new[] { "RESULT: ldarga.s 2", "ldc.i4.1", "stind.i4", "ldarg.2" }
            : ["RESULT: ldarg.2"]),
        "endfilter",
        "} handler {",
        "pop",
        "ldarg.0",
        "ldc.i4.s 42",
        "stfld int32 ArgumentFilterArgument::Value",
        "leave DONE",
        "}",
        "DONE: ret",
        "}",
        "}",
    ];

    /// <summary>
    /// Builds a filter that chooses its result and replacement receiver on the same branches.
    /// </summary>
    /// <returns>The complete class declaration.</returns>
    public static string[] CorrelatedReceiverFilterSource() =>
    [
        ".class public CorrelatedFilterArgument {",
        ".field public initonly int32 Value",
        ".method public instance void .ctor(class CorrelatedFilterArgument other, bool accept) {",
        "ldarg.0",
        "call instance void object::.ctor()",
        ".try {",
        "ldnull",
        "throw",
        "} filter {",
        "pop",
        "ldarg.2",
        "brtrue ACCEPT",
        "ldc.i4.0",
        "ldarg.1",
        "br ASSIGN",
        "ACCEPT: ldc.i4.1",
        "ldarg.0",
        "ASSIGN: starg.s 0",
        "endfilter",
        "} handler {",
        "pop",
        "ldarg.0",
        "ldc.i4.s 42",
        "stfld int32 CorrelatedFilterArgument::Value",
        "leave DONE",
        "}",
        "DONE: ret",
        "}",
        "}",
    ];

    /// <summary>
    /// Builds a filter that branches on its receiver-correlated result before returning it.
    /// </summary>
    /// <param name="branchOnTrue">Whether the result is tested with <c>brtrue</c> instead of <c>brfalse</c>.</param>
    /// <returns>The complete class declaration.</returns>
    public static string[] BranchedFilterDecisionSource(bool branchOnTrue) =>
    [
        ".class public BranchedFilterArgument {",
        ".field public initonly int32 Value",
        ".method public instance void .ctor(class BranchedFilterArgument other, bool accept) {",
        "ldarg.0",
        "call instance void object::.ctor()",
        ".try {",
        "ldnull",
        "throw",
        "} filter {",
        "pop",
        "ldarg.2",
        "brtrue ACCEPT",
        "ldarg.1",
        "starg.s 0",
        "ldc.i4.0",
        "br RESULT",
        "ACCEPT: ldc.i4.1",
        $"RESULT: {(branchOnTrue ? "brtrue ONE" : "brfalse ZERO")}",
        .. (branchOnTrue
            ? new[] { "ldc.i4.0", "br FINISH", "ONE: ldc.i4.1" }
            : ["ldc.i4.1", "br FINISH", "ZERO: ldc.i4.0"]),
        "FINISH: endfilter",
        "} handler {",
        "pop",
        "ldarg.0",
        "ldc.i4.s 42",
        "stfld int32 BranchedFilterArgument::Value",
        "leave DONE",
        "}",
        "DONE: ret",
        "}",
        "}",
    ];

    /// <summary>
    /// Builds a rejecting filter whose changed receiver continues into a later clause.
    /// </summary>
    /// <param name="throwFromFilter">Whether the filter rejects by throwing instead of returning zero.</param>
    /// <param name="secondFilter">Whether the later clause is an accepting filter instead of a catch.</param>
    /// <returns>The complete class declaration.</returns>
    public static string[] SiblingFilterSource(bool throwFromFilter = false, bool secondFilter = false) =>
    [
        ".class public SiblingFilterArgument {",
        ".field public initonly int32 Value",
        ".method public instance void .ctor(class SiblingFilterArgument other) {",
        "ldarg.0",
        "call instance void object::.ctor()",
        ".try {",
        "ldnull",
        "throw",
        "} filter {",
        "pop",
        "ldarg.1",
        "starg.s 0",
        .. (throwFromFilter ? new[] { "ldnull", "throw", "ldc.i4.0" } : ["ldc.i4.0"]),
        "endfilter",
        "} handler {",
        "pop",
        "leave DONE",
        .. (secondFilter
            ? new[] { "} filter {", "pop", "ldc.i4.1", "endfilter", "} handler {" }
            : ["} catch object {"]),
        "pop",
        "ldarg.0",
        "ldc.i4.s 42",
        "stfld int32 SiblingFilterArgument::Value",
        "leave DONE",
        "}",
        "DONE: ret",
        "}",
        "}",
    ];

    /// <summary>
    /// Builds a filter with enough independent decisions to exercise the path-state bound.
    /// </summary>
    /// <param name="diamonds">The number of independent decision diamonds.</param>
    /// <returns>The complete class declaration.</returns>
    public static string[] ManyFilterPathsSource(int diamonds)
    {
        var lines = new List<string>
        {
            ".class public ManyFilterPathsArgument {",
            ".field public initonly int32 Value",
            ".method public instance void .ctor(bool choose) {",
            $".locals init ({string.Join(", ", Enumerable.Range(0, diamonds).Select(index => $"int32 V_{index}"))})",
            "ldarg.0",
            "call instance void object::.ctor()",
            ".try {",
            "ldnull",
            "throw",
            "} filter {",
            "pop",
        };
        for (var index = 0; index < diamonds; index++)
        {
            lines.Add("ldarg.1");
            lines.Add($"brtrue D{index}_ONE");
            lines.Add("ldc.i4.0");
            lines.Add($"stloc {index}");
            lines.Add($"br D{index}_DONE");
            lines.Add($"D{index}_ONE: ldc.i4.1");
            lines.Add($"stloc {index}");
            lines.Add($"D{index}_DONE: nop");
        }

        lines.AddRange([
            "ldc.i4.1",
            "endfilter",
            "} handler {",
            "pop",
            "ldarg.0",
            "ldc.i4.s 42",
            "stfld int32 ManyFilterPathsArgument::Value",
            "leave DONE",
            "}",
            "DONE: ret",
            "}",
            "}",
        ]);
        return [.. lines];
    }

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
