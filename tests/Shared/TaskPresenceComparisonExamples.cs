namespace IlRepl.Tests.Shared;

/// <summary>
/// Builds methods whose null task returns differ from tasks that complete successfully.
/// </summary>
public static class TaskPresenceComparisonExamples
{
    /// <summary>
    /// Creates a method returning null or an actual completed task with the selected result type.
    /// </summary>
    /// <param name="kind">Zero selects Task, one selects Task of int32, and two selects Task of string.</param>
    /// <param name="present">Whether the method returns a completed task instead of null.</param>
    /// <returns>The complete method declaration.</returns>
    public static string Method(int kind, bool present)
    {
        var type = kind switch
        {
            0 => "class System.Threading.Tasks.Task",
            1 => "class System.Threading.Tasks.Task`1<int32>",
            _ => "class System.Threading.Tasks.Task`1<string>",
        };
        var body = !present ? "ldnull" : kind switch
        {
            0 => "call class System.Threading.Tasks.Task System.Threading.Tasks.Task::get_CompletedTask()",
            1 => "ldc.i4.s 42\ncall class System.Threading.Tasks.Task`1<!!0> System.Threading.Tasks.Task::FromResult<int32>(!!0)",
            _ => "ldnull\ncall class System.Threading.Tasks.Task`1<!!0> System.Threading.Tasks.Task::FromResult<string>(!!0)",
        };
        return ".method public static " + type + " Read() cil managed {\n" + body + "\nret\n}";
    }

    /// <summary>
    /// Creates a scenario that discards the selected task and returns the same independent value.
    /// </summary>
    /// <returns>The complete parameterless scenario.</returns>
    public static string IgnoredScenario() => """
        .method int32 Scenario() {
          call Copy
          pop
          ldc.i4.s 42
          ret
        }
        """;

    /// <summary>
    /// Creates a scenario that distinguishes an actual null task from a task object.
    /// </summary>
    /// <returns>The complete parameterless scenario.</returns>
    public static string BranchScenario() => """
        .method int32 Scenario() {
          call Copy
          brtrue.s PRESENT
          ldc.i4.s 41
          ret
        PRESENT:
          ldc.i4.s 42
          ret
        }
        """;
}
