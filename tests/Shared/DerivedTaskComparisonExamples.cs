namespace IlRepl.Tests.Shared;

/// <summary>
/// Builds real derived tasks that complete after the selected method returns to its scenario.
/// </summary>
public static class DerivedTaskComparisonExamples
{
    /// <summary>
    /// Declares a task subclass and a selected method that returns a pending instance.
    /// </summary>
    /// <param name="kind">Zero returns Task, one returns Task of int32, and two uses a generic subclass.</param>
    /// <param name="fail">Whether the task throws after mutating its input.</param>
    /// <returns>The complete type declarations.</returns>
    public static string Source(int kind, bool fail = false)
    {
        var generic = kind != 0;
        var work = kind == 2 ? "class Work`1<!0>" : "class Work";
        var result = kind == 2 ? "!0" : "int32";
        var task = "class Task" + (generic ? "`1<" + result + ">" : "");
        var callback = generic ? "class Func`1<" + result + ">" : "class Action";
        var returned = generic ? result : "void";
        var parameters = "int32[] state, int32 number" + (generic ? ", " + result + " value" : "");
        var completion = fail ? "ldstr \"derived failure\"\nnewobj instance void InvalidOperationException::.ctor(string)\nthrow"
            : generic ? "ldarg.0\nldfld " + result + " " + work + "::Value\nret" : "ret";
        return $$"""
            .class public Work{{(kind == 2 ? "<T>" : "")}} extends {{task}} {
              .field public int32[] State
              .field public int32 Number
              {{(generic ? ".field public " + result + " Value" : "")}}
              .method public instance {{returned}} Finish() {
                ldarg.0
                ldfld int32[] {{work}}::State
                ldc.i4.0
                ldarg.0
                ldfld int32 {{work}}::Number
                stelem.i4
                {{completion}}
              }
              .method public specialname rtspecialname instance void .ctor({{parameters}}) {
                ldarg.0
                ldarg.0
                ldftn instance {{returned}} {{work}}::Finish()
                newobj instance void {{callback}}::.ctor(object, native int)
                call instance void {{task}}::.ctor({{callback}})
                ldarg.0
                ldarg.1
                stfld int32[] {{work}}::State
                ldarg.0
                ldarg.2
                stfld int32 {{work}}::Number
                {{(generic ? "ldarg.0\nldarg.3\nstfld " + result + " " + work + "::Value" : "")}}
                ret
              }
            }
            .class public Owner {
              .field public static class Task Last
              {{Method(kind, 42)}}
            }
            """;
    }

    /// <summary>
    /// Creates the selected method with its task result and deferred mutation.
    /// </summary>
    /// <param name="kind">The derived task shape.</param>
    /// <param name="value">The number written when the task completes.</param>
    /// <returns>The selected method declaration.</returns>
    public static string Method(int kind, int value)
    {
        var work = kind == 2 ? "class Work`1<int32>" : "class Work";
        return ".method public static " + work + " Read(int32[] state) cil managed {\nldarg.0\nldc.i4 " + value + "\n"
            + (kind != 0 ? "ldc.i4 " + value + "\n" : "") + "newobj instance void " + work + "::.ctor(int32[], int32"
            + (kind == 2 ? ", !0" : kind == 1 ? ", int32" : "") + ")\ndup\nstsfld class Task Owner::Last\nret\n}";
    }

    /// <summary>
    /// Checks task identity before completing the task and returning it for the worker to await.
    /// </summary>
    /// <param name="complete">Whether the scenario completes the pending task.</param>
    /// <returns>The complete parameterless scenario.</returns>
    public static string Scenario(bool complete = true) => """
        .method class Task Scenario() {
          .locals init (class Task pending)
          ldc.i4.1
          newarr int32
          call Copy
          stloc.0
          ldloc.0
          ldsfld class Task IlRepl.Edits.Copy.Owner::Last
          call bool Object::ReferenceEquals(object, object)
          brtrue.s SAME
          ldstr "task identity changed"
          newobj instance void InvalidOperationException::.ctor(string)
          throw
        SAME:
        """ + (complete ? "\nldloc.0\ncallvirt instance void Task::RunSynchronously()\nldloc.0\n" : "\nldnull\n") + "ret\n}";
}
