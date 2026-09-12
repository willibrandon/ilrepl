namespace IlRepl.Tests.Shared;

/// <summary>
/// Supplies constructor branches whose later edge changes the receiver of an earlier readonly field store.
/// </summary>
public static class ControlFlowReceiverExamples
{
    /// <summary>
    /// Builds a finalizer whose receiver choice follows a correlated Boolean branch.
    /// </summary>
    /// <param name="matching">Whether each finalizer path restores the matching receiver.</param>
    /// <returns>The complete class declaration.</returns>
    public static string[] CorrelatedFinalizerSource(bool matching) =>
    [
        ".class public CorrelatedFinallyArgument {",
        ".field public initonly int32 Value",
        ".method public instance void .ctor(class CorrelatedFinallyArgument other, bool choice) {",
        ".locals init (class CorrelatedFinallyArgument first, class CorrelatedFinallyArgument second, int32 savedChoice)",
        "ldarg.0",
        "call instance void object::.ctor()",
        "ldarg choice",
        "brtrue SECOND",
        "ldarg.0",
        "stloc first",
        "ldarg other",
        "stloc second",
        "ldc.i4.0",
        "stloc savedChoice",
        "br BEFORE",
        "SECOND: ldarg other",
        "stloc first",
        "ldarg.0",
        "stloc second",
        "ldc.i4.1",
        "stloc savedChoice",
        "BEFORE: nop",
        ".try {",
        "leave DONE",
        "} finally {",
        "ldloc savedChoice",
        "brtrue USE_SECOND",
        matching ? "ldloc first" : "ldloc second",
        "starg.s 0",
        "br FINISH",
        matching ? "USE_SECOND: ldloc second" : "USE_SECOND: ldloc first",
        "starg.s 0",
        "FINISH: endfinally",
        "}",
        "DONE: ldarg.0",
        "ldc.i4.s 42",
        "stfld int32 CorrelatedFinallyArgument::Value",
        "ret",
        "}",
        "}",
    ];

    /// <summary>
    /// Builds a finalizer whose selector is overwritten after receiver paths diverge.
    /// </summary>
    /// <param name="followsInversion">Whether the finalizer follows the inverted selector.</param>
    /// <returns>The complete class declaration.</returns>
    public static string[] OverwrittenFinalizerConditionSource(bool followsInversion) =>
    [
        ".class public OverwrittenFinalizerCondition {",
        ".field public initonly int32 Value",
        ".method public instance void .ctor(class OverwrittenFinalizerCondition other, bool choice) {",
        ".locals init (class OverwrittenFinalizerCondition first, class OverwrittenFinalizerCondition second)",
        "ldarg.0",
        "call instance void object::.ctor()",
        "ldarg choice",
        "brtrue SECOND",
        "ldarg.0",
        "stloc first",
        "ldarg other",
        "stloc second",
        "br OVERWRITE",
        "SECOND: ldarg other",
        "stloc first",
        "ldarg.0",
        "stloc second",
        "OVERWRITE: ldarg choice",
        "ldc.i4.0",
        "ceq",
        "starg choice",
        ".try {",
        "leave DONE",
        "} finally {",
        "ldarg choice",
        "brtrue BRANCH",
        followsInversion ? "ldloc second" : "ldloc first",
        "starg.s 0",
        "br FINISH",
        followsInversion ? "BRANCH: ldloc first" : "BRANCH: ldloc second",
        "starg.s 0",
        "FINISH: endfinally",
        "}",
        "DONE: ldarg.0",
        "ldc.i4.s 42",
        "stfld int32 OverwrittenFinalizerCondition::Value",
        "ret",
        "}",
        "}",
    ];

    /// <summary>
    /// Builds a finalizer whose receiver choice follows a correlated switch.
    /// </summary>
    /// <param name="matching">Whether each finalizer path restores the matching receiver.</param>
    /// <returns>The complete class declaration.</returns>
    public static string[] CorrelatedSwitchFinalizerSource(bool matching) =>
    [
        ".class public CorrelatedSwitchFinallyArgument {",
        ".field public initonly int32 Value",
        ".method public instance void .ctor(class CorrelatedSwitchFinallyArgument other, int32 choice) {",
        ".locals init (class CorrelatedSwitchFinallyArgument first, class CorrelatedSwitchFinallyArgument second)",
        "ldarg.0",
        "call instance void object::.ctor()",
        "ldarg choice",
        "switch (CASE_ZERO)",
        "ldarg other",
        "stloc first",
        "ldarg.0",
        "stloc second",
        "br BEFORE",
        "CASE_ZERO: ldarg.0",
        "stloc first",
        "ldarg other",
        "stloc second",
        "BEFORE: nop",
        ".try {",
        "leave DONE",
        "} finally {",
        "ldarg choice",
        "switch (USE_FIRST)",
        matching ? "ldloc second" : "ldloc first",
        "starg.s 0",
        "br FINISH",
        matching ? "USE_FIRST: ldloc first" : "USE_FIRST: ldloc second",
        "starg.s 0",
        "FINISH: endfinally",
        "}",
        "DONE: ldarg.0",
        "ldc.i4.s 42",
        "stfld int32 CorrelatedSwitchFinallyArgument::Value",
        "ret",
        "}",
        "}",
    ];

    /// <summary>
    /// Builds a filter whose result is correlated with its receiver assignment.
    /// </summary>
    /// <param name="matching">Whether the path with a replaced receiver rejects the exception.</param>
    /// <returns>The complete class declaration.</returns>
    public static string[] RepeatedFilterConditionSource(bool matching) =>
    [
        ".class public RepeatedFilterCondition {",
        ".field public initonly int32 Value",
        ".method public instance void .ctor(class RepeatedFilterCondition other, bool accept) {",
        "ldarg.0",
        "call instance void object::.ctor()",
        ".try {",
        "ldnull",
        "throw",
        "} filter {",
        "pop",
        "ldarg accept",
        "brtrue PRESERVE",
        "ldarg other",
        "starg.s 0",
        "br DECIDE",
        "PRESERVE: nop",
        matching ? "DECIDE: ldarg accept" : "DECIDE: ldc.i4.1",
        "endfilter",
        "} handler {",
        "pop",
        "ldarg.0",
        "ldc.i4.s 42",
        "stfld int32 RepeatedFilterCondition::Value",
        "leave DONE",
        "}",
        "DONE: ret",
        "}",
        "}",
    ];

    /// <summary>
    /// Builds a filter whose switch case shares its target with the default edge.
    /// </summary>
    /// <param name="matching">Whether only the path with the original receiver accepts.</param>
    /// <returns>The complete class declaration.</returns>
    public static string[] SwitchFallthroughConditionSource(bool matching) =>
    [
        ".class public SwitchFallthroughCondition {",
        ".field public initonly int32 Value",
        ".method public instance void .ctor(class SwitchFallthroughCondition other, int32 choice) {",
        "ldarg.0",
        "call instance void object::.ctor()",
        ".try {",
        "ldnull",
        "throw",
        "} filter {",
        "pop",
        "ldarg choice",
        "switch (CHANGE, PRESERVE)",
        "CHANGE: ldarg other",
        "starg.s 0",
        "br DECIDE",
        "PRESERVE: nop",
        "DECIDE: ldarg choice",
        "switch (REJECT, ACCEPT)",
        matching ? "REJECT: ldc.i4.0" : "REJECT: ldc.i4.1",
        "br RESULT",
        matching ? "ACCEPT: ldc.i4.1" : "ACCEPT: ldc.i4.0",
        "RESULT: endfilter",
        "} handler {",
        "pop",
        "ldarg.0",
        "ldc.i4.s 42",
        "stfld int32 SwitchFallthroughCondition::Value",
        "leave DONE",
        "}",
        "DONE: ret",
        "}",
        "}",
    ];

    /// <summary>
    /// Builds a filter whose switch case and default share an unsafe receiver path.
    /// </summary>
    /// <returns>The complete class declaration.</returns>
    public static string[] UnsafeSharedSwitchTargetSource() =>
    [
        ".class public UnsafeSharedSwitchTarget {",
        ".field public initonly int32 Value",
        ".method public instance void .ctor(class UnsafeSharedSwitchTarget other, int32 choice) {",
        "ldarg.0",
        "call instance void object::.ctor()",
        ".try {",
        "ldnull",
        "throw",
        "} filter {",
        "pop",
        "ldarg choice",
        "switch (SHARED)",
        "SHARED: ldarg other",
        "starg.s 0",
        "ldarg choice",
        "switch (UNSAFE)",
        "ldc.i4.0",
        "br RESULT",
        "UNSAFE: ldc.i4.1",
        "RESULT: endfilter",
        "} handler {",
        "pop",
        "ldarg.0",
        "ldc.i4.s 42",
        "stfld int32 UnsafeSharedSwitchTarget::Value",
        "leave DONE",
        "}",
        "DONE: ret",
        "}",
        "}",
    ];

    /// <summary>
    /// Builds a conditional finalizer that may restore a replaced receiver.
    /// </summary>
    /// <param name="matching">Whether the finalizer restores the receiver on the changed path.</param>
    /// <returns>The complete class declaration.</returns>
    public static string[] ConditionalFinalizerSource(bool matching) =>
    [
        ".class public ConditionalFinalizer {",
        ".field public initonly int32 Value",
        ".method public instance void .ctor(class ConditionalFinalizer other, bool choice) {",
        ".locals init (class ConditionalFinalizer saved)",
        "ldarg.0",
        "call instance void object::.ctor()",
        "ldarg.0",
        "stloc saved",
        "ldarg choice",
        "brtrue PRESERVE",
        "ldarg other",
        "starg.s 0",
        "PRESERVE: nop",
        ".try {",
        "leave DONE",
        "} finally {",
        "ldarg choice",
        matching ? "brtrue KEEP" : "brfalse KEEP",
        "ldloc saved",
        "starg.s 0",
        "KEEP: endfinally",
        "}",
        "DONE: ldarg.0",
        "ldc.i4.s 42",
        "stfld int32 ConditionalFinalizer::Value",
        "ret",
        "}",
        "}",
    ];

    /// <summary>
    /// Builds sibling exception paths whose unwind effects exceed the detailed path limit.
    /// </summary>
    /// <returns>The complete class declaration.</returns>
    public static string[] BoundedUnwindContextsSource()
    {
        const int Paths = 66;
        var lines = new List<string>
        {
            ".class public BoundedUnwindContexts {",
            ".field public initonly int32 Value",
            ".method public instance void .ctor(class BoundedUnwindContexts other, int32 choice) {",
            ".locals init (class BoundedUnwindContexts saved)",
            "ldarg.0",
            "call instance void object::.ctor()",
            "ldarg.0",
            "stloc saved",
            ".try {",
        };
        for (var index = 0; index < Paths; index++)
        {
            lines.Add("ldarg choice");
            lines.Add($"ldc.i4 {index}");
            lines.Add($"bne.un NEXT_{index}");
            lines.Add(".try {");
            if (index == Paths - 2)
            {
                lines.Add("ldarg other");
                lines.Add("stloc saved");
            }
            lines.Add("ldnull");
            lines.Add("throw");
            lines.Add("} finally {");
            if (index == Paths - 2)
            {
                lines.Add($"LOOP_{index}: br LOOP_{index}");
            }
            else
            {
                lines.Add("ldloc saved");
                lines.Add("starg.s 0");
                lines.Add("endfinally");
            }
            lines.Add("}");
            lines.Add($"NEXT_{index}: nop");
        }
        lines.Add("leave DONE");
        lines.Add("} filter {");
        lines.Add("pop");
        lines.Add("ldc.i4.1");
        lines.Add("endfilter");
        lines.Add("} handler {");
        lines.Add("pop");
        lines.Add("ldarg.0");
        lines.Add("ldc.i4.s 42");
        lines.Add("stfld int32 BoundedUnwindContexts::Value");
        lines.Add("leave DONE");
        lines.Add("}");
        lines.Add("DONE: ret");
        lines.Add("}");
        lines.Add("}");
        return [.. lines];
    }

    /// <summary>
    /// Builds a finalizer whose possible receiver sources exceed the detailed path limit.
    /// </summary>
    /// <returns>The complete class declaration.</returns>
    public static string[] BoundedFinalizerSourcesSource()
    {
        const int Sources = 66;
        var parameters = string.Join(", ", Enumerable.Range(1, Sources)
            .Select(index => $"class BoundedFinalizerSources p{index}"));
        var labels = string.Join(", ", Enumerable.Range(0, Sources).Select(index => $"PICK_{index}"));
        var lines = new List<string>
        {
            ".class public BoundedFinalizerSources {",
            ".field public initonly int32 Value",
            $".method public instance void .ctor({parameters}, int32 choice) {{",
            "ldarg.0",
            "call instance void object::.ctor()",
        };
        for (var index = 1; index <= Sources; index++)
        {
            lines.Add("ldarg.0");
            lines.Add($"starg {index}");
        }
        lines.Add(".try {");
        lines.Add("leave DONE");
        lines.Add("} finally {");
        lines.Add($"ldarg {Sources + 1}");
        lines.Add($"switch ({labels})");
        lines.Add("br PICK_0");
        for (var index = 0; index < Sources; index++)
        {
            lines.Add($"PICK_{index}: ldarg {index + 1}");
            lines.Add("starg.s 0");
            lines.Add("br FINAL");
        }
        lines.Add("FINAL: endfinally");
        lines.Add("}");
        lines.Add("DONE: ldarg.0");
        lines.Add("ldc.i4.s 42");
        lines.Add("stfld int32 BoundedFinalizerSources::Value");
        lines.Add("ret");
        lines.Add("}");
        lines.Add("}");
        return [.. lines];
    }

    /// <summary>
    /// Builds more correlated receiver-to-finalizer pairs than the detailed path limit retains.
    /// </summary>
    /// <returns>The complete class declaration.</returns>
    public static string[] BoundedDistinctUnwindEffectsSource()
    {
        const int Paths = 66;
        var parameters = string.Join(", ", Enumerable.Range(1, Paths)
            .Select(index => $"class BoundedDistinctUnwindEffects p{index}"));
        var lines = new List<string>
        {
            ".class public BoundedDistinctUnwindEffects {",
            ".field public initonly int32 Value",
            $".method public instance void .ctor({parameters}, int32 choice) {{",
            "ldarg.0",
            "call instance void object::.ctor()",
            ".try {",
        };
        for (var index = 0; index < Paths; index++)
        {
            lines.Add($"ldarg {Paths + 1}");
            lines.Add($"ldc.i4 {index}");
            lines.Add($"bne.un NEXT_{index}");
            lines.Add(".try {");
            lines.Add("ldarg.0");
            lines.Add($"starg {index + 1}");
            lines.Add("ldnull");
            lines.Add("throw");
            lines.Add("} finally {");
            lines.Add($"ldarg {index + 1}");
            lines.Add("starg.s 0");
            lines.Add("endfinally");
            lines.Add("}");
            lines.Add($"NEXT_{index}: nop");
        }
        lines.Add("leave DONE");
        lines.Add("} filter {");
        lines.Add("pop");
        lines.Add("ldc.i4.1");
        lines.Add("endfilter");
        lines.Add("} handler {");
        lines.Add("pop");
        lines.Add("ldarg.0");
        lines.Add("ldc.i4.s 42");
        lines.Add("stfld int32 BoundedDistinctUnwindEffects::Value");
        lines.Add("leave DONE");
        lines.Add("}");
        lines.Add("DONE: ret");
        lines.Add("}");
        lines.Add("}");
        return [.. lines];
    }

    /// <summary>
    /// Builds enough distinct pending local writes to exceed the correlated path limit.
    /// </summary>
    /// <param name="unsafeParameter">The parameter left distinct from the original receiver.</param>
    /// <returns>The complete class declaration.</returns>
    public static string[] WidenedFinalizerLocalSource(int unsafeParameter)
    {
        const int Paths = 130;
        var parameters = string.Join(", ", Enumerable.Range(1, Paths)
            .Select(index => $"class WidenedFinalizerLocal p{index}"));
        var lines = new List<string>
        {
            ".class public WidenedFinalizerLocal {",
            ".field public initonly int32 Value",
            $".method public instance void .ctor({parameters}, int32 choice) {{",
            ".locals init (class WidenedFinalizerLocal saved)",
            "ldarg.0",
            "call instance void object::.ctor()",
            "ldarg.0",
            "stloc saved",
        };
        for (var index = 1; index <= Paths; index++)
        {
            if (index == unsafeParameter)
            {
                continue;
            }

            lines.Add("ldarg.0");
            lines.Add($"starg p{index}");
        }
        lines.Add(".try {");
        for (var index = 0; index < Paths; index++)
        {
            lines.Add("ldarg choice");
            lines.Add($"ldc.i4 {index}");
            lines.Add("sub");
            lines.Add($"brtrue NEXT_{index}");
            lines.Add(".try {");
            lines.Add("ldnull");
            lines.Add("throw");
            lines.Add("} finally {");
            lines.Add($"ldarg p{index + 1}");
            lines.Add("stloc saved");
            lines.Add("endfinally");
            lines.Add("}");
            lines.Add($"NEXT_{index}: nop");
        }
        lines.Add("leave DONE");
        lines.Add("} filter {");
        lines.Add("pop");
        lines.Add("ldc.i4.1");
        lines.Add("endfilter");
        lines.Add("} handler {");
        lines.Add("pop");
        lines.Add("ldloc saved");
        lines.Add("starg.s 0");
        lines.Add("ldarg.0");
        lines.Add("ldc.i4.s 42");
        lines.Add("stfld int32 WidenedFinalizerLocal::Value");
        lines.Add("leave DONE");
        lines.Add("}");
        lines.Add("DONE: ret");
        lines.Add("}");
        lines.Add("}");
        return [.. lines];
    }

    /// <summary>
    /// Builds switch-correlated receiver paths around the detailed path limit.
    /// </summary>
    /// <param name="cases">The number of explicit switch cases.</param>
    /// <returns>The complete class declaration.</returns>
    public static string[] BoundedCorrelatedFinalizerSource(int cases)
    {
        var parameters = string.Join(", ", Enumerable.Range(1, cases)
            .Select(index => $"class BoundedCorrelatedFinalizer p{index}"));
        var paths = string.Join(", ", Enumerable.Range(0, cases).Select(index => $"PATH_{index}"));
        var finalizers = string.Join(", ", Enumerable.Range(0, cases).Select(index => $"FINAL_{index}"));
        var lines = new List<string>
        {
            ".class public BoundedCorrelatedFinalizer {",
            ".field public initonly int32 Value",
            $".method public instance void .ctor({parameters}, int32 choice) {{",
            "ldarg.0",
            "call instance void object::.ctor()",
            "ldarg choice",
            $"switch ({paths})",
            "br PATH_0",
        };
        for (var index = 0; index < cases; index++)
        {
            lines.Add($"PATH_{index}: ldarg.0");
            lines.Add($"starg p{index + 1}");
            lines.Add("br BEFORE");
        }
        lines.Add("BEFORE: nop");
        lines.Add(".try {");
        lines.Add("leave DONE");
        lines.Add("} finally {");
        lines.Add("ldarg choice");
        lines.Add($"switch ({finalizers})");
        lines.Add("br FINAL_0");
        for (var index = 0; index < cases; index++)
        {
            lines.Add($"FINAL_{index}: ldarg p{index + 1}");
            lines.Add("starg.s 0");
            lines.Add("br FINISH");
        }
        lines.Add("FINISH: endfinally");
        lines.Add("}");
        lines.Add("DONE: ldarg.0");
        lines.Add("ldc.i4.s 42");
        lines.Add("stfld int32 BoundedCorrelatedFinalizer::Value");
        lines.Add("ret");
        lines.Add("}");
        lines.Add("}");
        return [.. lines];
    }

    /// <summary>
    /// Builds jointly exclusive Boolean receiver paths beside the detailed path limit.
    /// </summary>
    /// <param name="matching">Whether every joint condition restores its matching receiver.</param>
    /// <returns>The complete class declaration.</returns>
    public static string[] JointConditionFinalizerSource(bool matching)
    {
        const int Fillers = 63;
        var fillers = string.Join(", ", Enumerable.Range(0, Fillers).Select(index => $"FILLER_{index}"));
        var finishers = string.Join(", ", Enumerable.Repeat("FINISH", Fillers));
        var lines = new List<string>
        {
            ".class public JointConditionFinalizer {",
            ".field public initonly int32 Value",
            ".method public instance void .ctor(class JointConditionFinalizer other, int32 filler, bool a, bool b) {",
            ".locals init (class JointConditionFinalizer first, class JointConditionFinalizer second)",
            "ldarg.0",
            "call instance void object::.ctor()",
            "ldarg filler",
            $"switch ({fillers})",
            "br CONDITIONS",
        };
        for (var index = 0; index < Fillers; index++)
        {
            lines.Add($"FILLER_{index}: br BEFORE");
        }
        lines.Add("CONDITIONS: ldarg a");
        lines.Add("brtrue A_TRUE");
        lines.Add("ldarg b");
        lines.Add("brtrue MAP_SECOND");
        lines.Add("br MAP_FIRST");
        lines.Add("A_TRUE: ldarg b");
        lines.Add("brtrue MAP_FIRST");
        lines.Add("br MAP_SECOND");
        lines.Add("MAP_FIRST: ldarg.0");
        lines.Add("stloc first");
        lines.Add("ldarg other");
        lines.Add("stloc second");
        lines.Add("br BEFORE");
        lines.Add("MAP_SECOND: ldarg other");
        lines.Add("stloc first");
        lines.Add("ldarg.0");
        lines.Add("stloc second");
        lines.Add("BEFORE: nop");
        lines.Add(".try {");
        lines.Add("leave DONE");
        lines.Add("} finally {");
        lines.Add("ldarg filler");
        lines.Add($"switch ({finishers})");
        lines.Add("ldarg a");
        lines.Add("brtrue FINAL_A_TRUE");
        lines.Add("ldarg b");
        lines.Add("brtrue USE_SECOND");
        lines.Add("br USE_FIRST");
        lines.Add("FINAL_A_TRUE: ldarg b");
        lines.Add(matching ? "brtrue USE_FIRST" : "brtrue USE_SECOND");
        lines.Add("br USE_SECOND");
        lines.Add("USE_FIRST: ldloc first");
        lines.Add("starg.s 0");
        lines.Add("br FINISH");
        lines.Add("USE_SECOND: ldloc second");
        lines.Add("starg.s 0");
        lines.Add("FINISH: endfinally");
        lines.Add("}");
        lines.Add("DONE: ldarg.0");
        lines.Add("ldc.i4.s 42");
        lines.Add("stfld int32 JointConditionFinalizer::Value");
        lines.Add("ret");
        lines.Add("}");
        lines.Add("}");
        return [.. lines];
    }

    /// <summary>
    /// Builds a filter that repeats an integer equality test before choosing its receiver.
    /// </summary>
    /// <param name="matching">Whether the changed receiver is rejected by the repeated test.</param>
    /// <param name="directBranch">Whether the test uses direct equality branches instead of ceq.</param>
    /// <param name="constant">The integer compared with the selector.</param>
    /// <returns>The complete class declaration.</returns>
    public static string[] RepeatedConstantComparisonSource(
        bool matching, bool directBranch, int constant = 5) =>
    [
        ".class public RepeatedConstantComparison {",
        ".field public initonly int32 Value",
        ".method public instance void .ctor(class RepeatedConstantComparison other, int32 choice) {",
        "ldarg.0",
        "call instance void object::.ctor()",
        ".try {",
        "ldnull",
        "throw",
        "} filter {",
        "pop",
        "ldarg choice",
        $"ldc.i4 {constant}",
        .. (directBranch ? new[] { "bne.un PRESERVE" } : ["ceq", "brfalse PRESERVE"]),
        "ldarg other",
        "starg.s 0",
        "PRESERVE: ldarg choice",
        $"ldc.i4 {constant}",
        .. (directBranch
            ? new[] { matching ? "beq REJECT" : "bne.un REJECT" }
            : ["ceq", matching ? "brtrue REJECT" : "brfalse REJECT"]),
        "ldc.i4.1",
        "br RESULT",
        "REJECT: ldc.i4.0",
        "RESULT: endfilter",
        "} handler {",
        "pop",
        "ldarg.0",
        "ldc.i4.s 42",
        "stfld int32 RepeatedConstantComparison::Value",
        "leave DONE",
        "}",
        "DONE: ret",
        "}",
        "}",
    ];

    /// <summary>
    /// Builds a filter that repeats a null comparison before choosing its receiver.
    /// </summary>
    /// <param name="matching">Whether the changed receiver is rejected by the repeated test.</param>
    /// <returns>The complete class declaration.</returns>
    public static string[] RepeatedNullComparisonSource(bool matching) =>
    [
        ".class public RepeatedNullComparison {",
        ".field public initonly int32 Value",
        ".method public instance void .ctor(class RepeatedNullComparison other, object choice) {",
        "ldarg.0",
        "call instance void object::.ctor()",
        ".try {",
        "ldnull",
        "throw",
        "} filter {",
        "pop",
        "ldarg choice",
        "ldnull",
        "ceq",
        "brfalse PRESERVE",
        "ldarg other",
        "starg.s 0",
        "PRESERVE: ldarg choice",
        "ldnull",
        "ceq",
        matching ? "brtrue REJECT" : "brfalse REJECT",
        "ldc.i4.1",
        "br RESULT",
        "REJECT: ldc.i4.0",
        "RESULT: endfilter",
        "} handler {",
        "pop",
        "ldarg.0",
        "ldc.i4.s 42",
        "stfld int32 RepeatedNullComparison::Value",
        "leave DONE",
        "}",
        "DONE: ret",
        "}",
        "}",
    ];

    /// <summary>
    /// Builds a filter that repeats an equality test between two reference arguments.
    /// </summary>
    /// <param name="matching">Whether the changed receiver is rejected by the repeated test.</param>
    /// <returns>The complete class declaration.</returns>
    public static string[] RepeatedSourceComparisonSource(bool matching) =>
    [
        ".class public RepeatedSourceComparison {",
        ".field public initonly int32 Value",
        ".method public instance void .ctor(class RepeatedSourceComparison other, object choice, object sentinel) {",
        "ldarg.0",
        "call instance void object::.ctor()",
        ".try {",
        "ldnull",
        "throw",
        "} filter {",
        "pop",
        "ldarg choice",
        "ldarg sentinel",
        "ceq",
        "brfalse PRESERVE",
        "ldarg other",
        "starg.s 0",
        "PRESERVE: ldarg choice",
        "ldarg sentinel",
        "ceq",
        matching ? "brtrue REJECT" : "brfalse REJECT",
        "ldc.i4.1",
        "br RESULT",
        "REJECT: ldc.i4.0",
        "RESULT: endfilter",
        "} handler {",
        "pop",
        "ldarg.0",
        "ldc.i4.s 42",
        "stfld int32 RepeatedSourceComparison::Value",
        "leave DONE",
        "}",
        "DONE: ret",
        "}",
        "}",
    ];

    /// <summary>
    /// Builds a filter that relies on transitive equality between three reference arguments.
    /// </summary>
    /// <param name="matching">Whether the transitive equality rejects the changed receiver.</param>
    /// <returns>The complete class declaration.</returns>
    public static string[] TransitiveSourceComparisonSource(bool matching) =>
    [
        ".class public TransitiveSourceComparison {",
        ".field public initonly int32 Value",
        ".method public instance void .ctor(class TransitiveSourceComparison other, object a, object b, object c) {",
        "ldarg.0",
        "call instance void object::.ctor()",
        ".try {",
        "ldnull",
        "throw",
        "} filter {",
        "pop",
        "ldarg a",
        "ldarg b",
        "ceq",
        "brfalse ACCEPT",
        "ldarg b",
        "ldarg c",
        "ceq",
        "brfalse ACCEPT",
        "ldarg other",
        "starg.s 0",
        "ldarg a",
        "ldarg c",
        "ceq",
        matching ? "brtrue REJECT" : "brfalse REJECT",
        "ACCEPT: ldc.i4.1",
        "br RESULT",
        "REJECT: ldc.i4.0",
        "RESULT: endfilter",
        "} handler {",
        "pop",
        "ldarg.0",
        "ldc.i4.s 42",
        "stfld int32 TransitiveSourceComparison::Value",
        "leave DONE",
        "}",
        "DONE: ret",
        "}",
        "}",
    ];

    /// <summary>
    /// Builds a filter that repeats a floating-point value's self-comparison.
    /// </summary>
    /// <param name="matching">Whether the unordered path rejects its changed receiver.</param>
    /// <returns>The complete class declaration.</returns>
    public static string[] RepeatedFloatingComparisonSource(bool matching) =>
    [
        ".class public RepeatedFloatingComparison {",
        ".field public initonly int32 Value",
        ".method public instance void .ctor(class RepeatedFloatingComparison other, float64 choice) {",
        "ldarg.0",
        "call instance void object::.ctor()",
        ".try {",
        "ldnull",
        "throw",
        "} filter {",
        "pop",
        "ldarg choice",
        "ldarg choice",
        "beq PRESERVE",
        "ldarg other",
        "starg.s 0",
        "PRESERVE: ldarg choice",
        "ldarg choice",
        matching ? "bne.un REJECT" : "beq REJECT",
        "ldc.i4.1",
        "br RESULT",
        "REJECT: ldc.i4.0",
        "RESULT: endfilter",
        "} handler {",
        "pop",
        "ldarg.0",
        "ldc.i4.s 42",
        "stfld int32 RepeatedFloatingComparison::Value",
        "leave DONE",
        "}",
        "DONE: ret",
        "}",
        "}",
    ];

    /// <summary>
    /// Builds a finalizer that restores argument zero from a receiver-bearing local before a field store.
    /// </summary>
    /// <param name="changed">Whether the saved local is replaced with another instance first.</param>
    /// <returns>The complete class declaration.</returns>
    public static string[] FinalizerIncomingLocalSource(bool changed) =>
    [
        ".class public FinalizerIncomingLocal {",
        ".field public initonly int32 Value",
        ".method public instance void .ctor(class FinalizerIncomingLocal other) {",
        ".locals init (class FinalizerIncomingLocal saved)",
        "ldarg.0",
        "call instance void object::.ctor()",
        changed ? "ldarg.1" : "ldarg.0",
        "stloc saved",
        ".try {",
        "leave DONE",
        "} finally {",
        "ldloc saved",
        "starg.s 0",
        "ldarg.0",
        "ldc.i4.s 42",
        "stfld int32 FinalizerIncomingLocal::Value",
        "endfinally",
        "}",
        "DONE: ret",
        "}",
        "}",
    ];

    /// <summary>
    /// Builds nested finalizers whose possible receiver sources exceed the detailed path limit.
    /// </summary>
    /// <returns>The complete class declaration.</returns>
    public static string[] BoundedFinalizerPathsSource()
    {
        const int Paths = 9;
        var receiverParameters = string.Join(", ", Enumerable.Range(1, Paths)
            .Select(index => $"class BoundedFinalizerPathsArgument p{index}"));
        var lines = new List<string>
        {
            ".class public BoundedFinalizerPathsArgument {",
            $".method public instance void .ctor({receiverParameters}, int32 first, int32 second) {{",
            "ldarg.0",
            "call instance void object::.ctor()",
            ".try {",
            ".try {",
            "leave DONE",
            "} finally {",
        };
        AddReceiverSwitch(lines, Paths, Paths + 1, "FIRST");
        lines.Add("}");
        lines.Add("} finally {");
        AddReceiverSwitch(lines, Paths, Paths + 2, "SECOND");
        lines.Add("}");
        lines.Add("DONE: ret");
        lines.Add("}");
        lines.Add("}");
        return [.. lines];
    }

    /// <summary>
    /// Builds nested finalizers that pass a changed receiver through another argument slot.
    /// </summary>
    /// <returns>The complete class declaration.</returns>
    public static string[] NestedFinalizerArgumentSource() =>
    [
        ".class public NestedFinalizerArgumentMap {",
        ".field public initonly int32 Value",
        ".method public instance void .ctor(class NestedFinalizerArgumentMap saved, class NestedFinalizerArgumentMap other) {",
        "ldarg.0",
        "call instance void object::.ctor()",
        "ldarg.0",
        "starg.s 1",
        ".try {",
        ".try {",
        "leave DONE",
        "} finally {",
        "ldarg.2",
        "starg.s 1",
        "}",
        "} finally {",
        "ldarg.1",
        "starg.s 0",
        "}",
        "DONE: ldarg.0",
        "ldc.i4.s 42",
        "stfld int32 NestedFinalizerArgumentMap::Value",
        "ret",
        "}",
        "}",
    ];

    /// <summary>
    /// Builds irrelevant filter diamonds before correlated accepting and rejecting receiver paths.
    /// </summary>
    /// <returns>The complete class declaration.</returns>
    public static string[] BoundedSelectiveFilterSource()
    {
        const int Diamonds = 7;
        var locals = string.Join(", ", Enumerable.Range(0, Diamonds).Select(index => $"int32 V_{index}"));
        var lines = new List<string>
        {
            ".class public BoundedSelectiveFilter {",
            ".field public initonly int32 Value",
            ".method public instance void .ctor(class BoundedSelectiveFilter other, bool accept) {",
            $".locals init ({locals})",
            "ldarg.0",
            "call instance void object::.ctor()",
            ".try {",
            "ldnull",
            "throw",
            "} filter {",
            "pop",
        };
        for (var index = 0; index < Diamonds; index++)
        {
            lines.Add("ldarg.2");
            lines.Add($"brtrue D{index}_ONE");
            lines.Add("ldc.i4.0");
            lines.Add($"stloc {index}");
            lines.Add($"br D{index}_DONE");
            lines.Add($"D{index}_ONE: ldc.i4.1");
            lines.Add($"stloc {index}");
            lines.Add($"D{index}_DONE: nop");
        }
        lines.Add("ldarg.2");
        lines.Add("brtrue ACCEPT");
        lines.Add("ldarg.1");
        lines.Add("starg.s 0");
        lines.Add("ldc.i4.0");
        lines.Add("br RESULT");
        lines.Add("ACCEPT: ldc.i4.1");
        lines.Add("RESULT: endfilter");
        lines.Add("} handler {");
        lines.Add("pop");
        lines.Add("ldarg.0");
        lines.Add("ldc.i4.s 42");
        lines.Add("stfld int32 BoundedSelectiveFilter::Value");
        lines.Add("leave DONE");
        lines.Add("}");
        lines.Add("DONE: ret");
        lines.Add("}");
        lines.Add("}");
        return [.. lines];
    }

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

    private static void AddReceiverSwitch(List<string> lines, int paths, int selector, string prefix)
    {
        var labels = string.Join(", ", Enumerable.Range(0, paths).Select(index => $"{prefix}{index}"));
        lines.Add($"ldarg {selector}");
        lines.Add($"switch ({labels})");
        lines.Add($"br {prefix}0");
        for (var index = 0; index < paths; index++)
        {
            lines.Add($"{prefix}{index}: ldarg {index + 1}");
            lines.Add("starg.s 0");
            lines.Add($"br {prefix}END");
        }
        lines.Add($"{prefix}END: endfinally");
    }

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
    /// Builds a constructor whose exception unwinds through a finalizer before an outer handler uses argument zero.
    /// </summary>
    /// <param name="originalReceiver">Whether the finalizer stores the original receiver.</param>
    /// <param name="fault">Whether the unwind handler is a fault instead of a finally.</param>
    /// <param name="filter">Whether the outer handler is selected by a filter.</param>
    /// <param name="completes">Whether the unwind handler reaches its implicit endfinally.</param>
    /// <returns>The complete class declaration.</returns>
    public static string[] ExceptionUnwindSource(bool originalReceiver, bool fault, bool filter,
        bool completes = true) =>
    [
        ".class public FlowExceptionUnwindArgument {",
        ".field public initonly int32 Value",
        ".method public instance void .ctor(class FlowExceptionUnwindArgument other) {",
        "ldarg.0",
        "call instance void object::.ctor()",
        ".try {",
        ".try {",
        "ldnull",
        "throw",
        $"}} {(fault ? "fault" : "finally")} {{",
        .. (completes
            ? new[] { originalReceiver ? "ldarg.0" : "ldarg.1", "starg.s 0" }
            : ["UNWIND: br UNWIND"]),
        "}",
        .. (filter ? new[] { "} filter {", "pop", "ldc.i4.1", "endfilter", "} handler {" }
            : ["} catch object {"]),
        "pop",
        "ldarg.0",
        "ldc.i4.s 42",
        "stfld int32 FlowExceptionUnwindArgument::Value",
        "leave DONE",
        "}",
        "DONE: ret",
        "}",
        "}",
    ];

    /// <summary>
    /// Builds a constructor whose fault is skipped by normal control flow before argument zero is used.
    /// </summary>
    /// <returns>The complete class declaration.</returns>
    public static string[] FaultLeaveSource() =>
    [
        ".class public FlowFaultLeaveArgument {",
        ".field public initonly int32 Value",
        ".method public instance void .ctor(class FlowFaultLeaveArgument other) {",
        "ldarg.0",
        "call instance void object::.ctor()",
        ".try {",
        "leave DONE",
        "} fault {",
        "ldarg.1",
        "starg.s 0",
        "}",
        "DONE: ldarg.0",
        "ldc.i4.s 42",
        "stfld int32 FlowFaultLeaveArgument::Value",
        "ret",
        "}",
        "}",
    ];

    /// <summary>
    /// Builds a catch or filter handler whose leave executes a trailing finally before its target.
    /// </summary>
    /// <param name="filter">Whether the first handler is selected by a filter.</param>
    /// <returns>The complete class declaration.</returns>
    public static string[] HandlerLeaveSource(bool filter) =>
    [
        ".class public FlowHandlerLeaveArgument {",
        ".field public initonly int32 Value",
        ".method public instance void .ctor(class FlowHandlerLeaveArgument other) {",
        "ldarg.0",
        "call instance void object::.ctor()",
        ".try {",
        "ldnull",
        "throw",
        .. (filter ? new[] { "} filter {", "pop", "ldc.i4.1", "endfilter", "} handler {" }
            : ["} catch object {"]),
        "pop",
        "leave DONE",
        "} finally {",
        "ldarg.1",
        "starg.s 0",
        "}",
        "DONE: ldarg.0",
        "ldc.i4.s 42",
        "stfld int32 FlowHandlerLeaveArgument::Value",
        "ret",
        "}",
        "}",
    ];

    /// <summary>
    /// Builds a filter that uses argument zero before a nested unwind changes it for the paired handler.
    /// </summary>
    /// <returns>The complete class declaration.</returns>
    public static string[] FilterUnwindTimingSource() =>
    [
        ".class public FlowFilterUnwindArgument {",
        ".field public initonly int32 Value",
        ".method public instance void .ctor(class FlowFilterUnwindArgument other) {",
        "ldarg.0",
        "call instance void object::.ctor()",
        ".try {",
        ".try {",
        "ldnull",
        "throw",
        "} finally {",
        "ldarg.1",
        "starg.s 0",
        "}",
        "} filter {",
        "pop",
        "ldarg.0",
        "ldc.i4.1",
        "stfld int32 FlowFilterUnwindArgument::Value",
        "ldc.i4.1",
        "endfilter",
        "} handler {",
        "pop",
        "ldarg.0",
        "ldc.i4.2",
        "stfld int32 FlowFilterUnwindArgument::Value",
        "leave DONE",
        "}",
        "DONE: ret",
        "}",
        "}",
    ];

    /// <summary>
    /// Builds two exception paths whose outer filter has different pending unwind handlers.
    /// </summary>
    /// <returns>The complete class declaration.</returns>
    public static string[] MixedExceptionUnwindSource() =>
    [
        ".class public FlowMixedUnwindArgument {",
        ".field public initonly int32 Value",
        ".method public instance void .ctor(class FlowMixedUnwindArgument other, bool direct) {",
        "ldarg.0",
        "call instance void object::.ctor()",
        ".try {",
        "ldarg.2",
        "brtrue DIRECT",
        ".try {",
        "ldnull",
        "throw",
        "} finally {",
        "UNWIND: br UNWIND",
        "}",
        "DIRECT: ldarg.1",
        "starg.s 0",
        "ldnull",
        "throw",
        "} filter {",
        "pop",
        "ldc.i4.1",
        "endfilter",
        "} handler {",
        "pop",
        "ldarg.0",
        "ldc.i4.s 42",
        "stfld int32 FlowMixedUnwindArgument::Value",
        "leave DONE",
        "}",
        "DONE: ret",
        "}",
        "}",
    ];

    /// <summary>
    /// Builds a first filter whose accepting branch alone changes the receiver before a later catch.
    /// </summary>
    /// <returns>The complete class declaration.</returns>
    public static string[] SelectiveSiblingFilterSource() =>
    [
        ".class public FlowSelectiveSiblingArgument {",
        ".field public initonly int32 Value",
        ".method public instance void .ctor(class FlowSelectiveSiblingArgument other, bool accept) {",
        "ldarg.0",
        "call instance void object::.ctor()",
        ".try {",
        "ldnull",
        "throw",
        "} filter {",
        "pop",
        "ldarg.2",
        "brfalse REJECT",
        "ldarg.1",
        "starg.s 0",
        "ldc.i4.1",
        "br RESULT",
        "REJECT: ldc.i4.0",
        "RESULT: endfilter",
        "} handler {",
        "pop",
        "leave DONE",
        "} catch object {",
        "pop",
        "ldarg.0",
        "ldc.i4.s 42",
        "stfld int32 FlowSelectiveSiblingArgument::Value",
        "leave DONE",
        "}",
        "DONE: ret",
        "}",
        "}",
    ];

    /// <summary>
    /// Builds rejecting filters that restore the original receiver before a later catch uses it.
    /// </summary>
    /// <param name="secondFilter">Whether a second filter performs the restoration.</param>
    /// <returns>The complete class declaration.</returns>
    public static string[] RestoringFilterSource(bool secondFilter) =>
    [
        ".class public RestoringFilterArgument {",
        ".field public initonly int32 Value",
        ".method public instance void .ctor(class RestoringFilterArgument saved, class RestoringFilterArgument other) {",
        "ldarg.0",
        "call instance void object::.ctor()",
        "ldarg.0",
        "starg.s 1",
        "ldarg.2",
        "starg.s 0",
        ".try {",
        "ldnull",
        "throw",
        "} filter {",
        "pop",
        .. (secondFilter ? [] : new[] { "ldarg.1", "starg.s 0" }),
        "ldc.i4.0",
        "endfilter",
        "} handler {",
        "pop",
        "leave DONE",
        .. (secondFilter
            ? new[]
            {
                "} filter {",
                "pop",
                "ldarg.1",
                "starg.s 0",
                "ldc.i4.0",
                "endfilter",
                "} handler {",
                "pop",
                "leave DONE",
            }
            : []),
        "} catch object {",
        "pop",
        "ldarg.0",
        "ldc.i4.s 42",
        "stfld int32 RestoringFilterArgument::Value",
        "leave DONE",
        "}",
        "DONE: ret",
        "}",
        "}",
    ];

    /// <summary>
    /// Builds an unwind handler that writes a readonly field after protected code replaces argument zero.
    /// </summary>
    /// <param name="fault">Whether the unwind handler is a fault instead of a finally.</param>
    /// <param name="exceptional">Whether the protected region throws instead of leaving normally.</param>
    /// <returns>The complete class declaration.</returns>
    public static string[] UnwindHandlerStoreSource(bool fault, bool exceptional) =>
    [
        ".class public UnwindStoreArgument {",
        ".field public initonly int32 Value",
        ".method public instance void .ctor(class UnwindStoreArgument other) {",
        "ldarg.0",
        "call instance void object::.ctor()",
        "ldarg.1",
        "starg.s 0",
        ".try {",
        ".try {",
        .. (exceptional ? new[] { "ldnull", "throw" } : ["leave DONE"]),
        $"}} {(fault ? "fault" : "finally")} {{",
        "ldarg.0",
        "ldc.i4.s 42",
        "stfld int32 UnwindStoreArgument::Value",
        "}",
        "} catch object {",
        "pop",
        "leave DONE",
        "}",
        "DONE: ret",
        "}",
        "}",
    ];

    /// <summary>
    /// Builds accepting filter paths with different receivers that enter the same pending finally.
    /// </summary>
    /// <returns>The complete class declaration.</returns>
    public static string[] FilterPathUnwindHandlerSource() =>
    [
        ".class public FilterPathUnwindArgument {",
        ".field public initonly int32 Value",
        ".method public instance void .ctor(class FilterPathUnwindArgument other, bool keep) {",
        "ldarg.0",
        "call instance void object::.ctor()",
        ".try {",
        ".try {",
        "ldnull",
        "throw",
        "} finally {",
        "ldarg.0",
        "ldc.i4.s 42",
        "stfld int32 FilterPathUnwindArgument::Value",
        "}",
        "} filter {",
        "pop",
        "ldarg.2",
        "brtrue KEEP",
        "ldarg.1",
        "starg.s 0",
        "KEEP: ldc.i4.1",
        "endfilter",
        "} handler {",
        "pop",
        "leave DONE",
        "}",
        "DONE: ret",
        "}",
        "}",
    ];

    /// <summary>
    /// Builds a finally that replaces the protected exception after argument zero was replaced.
    /// </summary>
    /// <returns>The complete class declaration.</returns>
    public static string[] ThrowingUnwindHandlerSource() =>
    [
        ".class public ThrowingUnwindArgument {",
        ".field public initonly int32 Value",
        ".method public instance void .ctor(class ThrowingUnwindArgument other) {",
        "ldarg.0",
        "call instance void object::.ctor()",
        "ldarg.1",
        "starg.s 0",
        ".try {",
        ".try {",
        "ldnull",
        "throw",
        "} finally {",
        "ldnull",
        "throw",
        "}",
        "} catch object {",
        "pop",
        "ldarg.0",
        "ldc.i4.s 42",
        "stfld int32 ThrowingUnwindArgument::Value",
        "leave DONE",
        "}",
        "DONE: ret",
        "}",
        "}",
    ];

    /// <summary>
    /// Builds a finally that restores a saved original receiver before the leave target uses it.
    /// </summary>
    /// <returns>The complete class declaration.</returns>
    public static string[] RestoringFinallySource() =>
    [
        ".class public RestoringFinallyArgument {",
        ".field public initonly int32 Value",
        ".method public instance void .ctor(class RestoringFinallyArgument saved, class RestoringFinallyArgument other) {",
        "ldarg.0",
        "call instance void object::.ctor()",
        "ldarg.0",
        "starg.s 1",
        "ldarg.2",
        "starg.s 0",
        ".try {",
        "leave DONE",
        "} finally {",
        "ldarg.1",
        "starg.s 0",
        "}",
        "DONE: ldarg.0",
        "ldc.i4.s 42",
        "stfld int32 RestoringFinallyArgument::Value",
        "ret",
        "}",
        "}",
    ];

    /// <summary>
    /// Builds a finally that restores an original receiver saved in a local before the protected region.
    /// </summary>
    /// <returns>The complete class declaration.</returns>
    public static string[] LocalRestoringFinallySource() =>
    [
        ".class public LocalRestoringFinallyArgument {",
        ".field public initonly int32 Value",
        ".method public instance void .ctor(class LocalRestoringFinallyArgument other) {",
        ".locals init (class LocalRestoringFinallyArgument saved)",
        "ldarg.0",
        "call instance void object::.ctor()",
        "ldarg.0",
        "stloc saved",
        "ldarg.1",
        "starg.s 0",
        ".try {",
        "leave DONE",
        "} finally {",
        "ldloc saved",
        "starg.s 0",
        "}",
        "DONE: ldarg.0",
        "ldc.i4.s 42",
        "stfld int32 LocalRestoringFinallyArgument::Value",
        "ret",
        "}",
        "}",
    ];

    /// <summary>
    /// Builds a finally that writes through argument zero's address before a readonly field store.
    /// </summary>
    /// <param name="write">The indirect write instruction.</param>
    /// <returns>The complete class declaration.</returns>
    public static string[] FinalizerAddressSource(string write) =>
    [
        ".class public FinalizerAddressArgument {",
        ".field public initonly int32 Value",
        ".method public instance void .ctor(class FinalizerAddressArgument other) {",
        "ldarg.0",
        "call instance void object::.ctor()",
        ".try {",
        "leave DONE",
        "} finally {",
        "ldarga.s 0",
        .. (write switch
        {
            "stind.ref" => new[] { "ldarg.1", "stind.ref" },
            "stobj" => ["ldarg.1", "stobj class FinalizerAddressArgument"],
            "initobj" => ["initobj class FinalizerAddressArgument"],
            "cpobj" => ["ldarga.s 1", "cpobj class FinalizerAddressArgument"],
            "initblk" => ["ldc.i4.0", "ldc.i4.8", "initblk"],
            "cpblk" => ["ldarga.s 1", "ldc.i4.8", "cpblk"],
            _ => throw new ArgumentOutOfRangeException(nameof(write)),
        }),
        "}",
        "DONE: ldarg.0",
        "ldc.i4.s 42",
        "stfld int32 FinalizerAddressArgument::Value",
        "ret",
        "}",
        "}",
    ];

    /// <summary>
    /// Builds a finally that reloads a saved receiver after its argument address was exposed.
    /// </summary>
    /// <returns>The complete class declaration.</returns>
    public static string[] FinalizerAliasedArgumentSource() =>
    [
        ".class public FinalizerAliasedArgument {",
        ".field public initonly int32 Value",
        ".method public instance void .ctor(class FinalizerAliasedArgument saved, class FinalizerAliasedArgument other) {",
        "ldarg.0",
        "call instance void object::.ctor()",
        "ldarg.0",
        "starg.s 1",
        "ldarg.2",
        "starg.s 0",
        ".try {",
        "leave DONE",
        "} finally {",
        "ldarga.s 1",
        "ldarg.2",
        "stind.ref",
        "ldarg.1",
        "starg.s 0",
        "}",
        "DONE: ldarg.0",
        "ldc.i4.s 42",
        "stfld int32 FinalizerAliasedArgument::Value",
        "ret",
        "}",
        "}",
    ];

    /// <summary>
    /// Builds a catch before a filter that would replace the receiver if exception search reached it.
    /// </summary>
    /// <param name="catchAll">Whether the first clause catches every exception.</param>
    /// <returns>The complete class declaration.</returns>
    public static string[] CatchBeforeFilterSource(bool catchAll) =>
    [
        ".class public CatchBeforeFilterArgument {",
        ".field public initonly int32 Value",
        ".method public instance void .ctor(class CatchBeforeFilterArgument other) {",
        "ldarg.0",
        "call instance void object::.ctor()",
        ".try {",
        "ldnull",
        "throw",
        $"}} catch {(catchAll ? "object" : "[System.Runtime]System.InvalidOperationException")} {{",
        "pop",
        "leave DONE",
        "} filter {",
        "pop",
        "ldarg.1",
        "starg.s 0",
        "ldc.i4.1",
        "endfilter",
        "} handler {",
        "pop",
        "ldarg.0",
        "ldc.i4.s 42",
        "stfld int32 CatchBeforeFilterArgument::Value",
        "leave DONE",
        "}",
        "DONE: ret",
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
