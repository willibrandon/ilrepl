namespace IlRepl.Tests.Shared;

/// <summary>
/// Supplies executable scenarios with mutations, differing inputs, and caught method exceptions.
/// </summary>
public static class ComparisonTranscriptExamples
{
    /// <summary>
    /// Declares the selected method and its receiver state.
    /// </summary>
    /// <param name="kind">The observation exercised by the scenario.</param>
    /// <returns>The complete original type declaration.</returns>
    public static string Source(string kind) => """
        .class public Owner {
          .field public int32 Value
          .method public instance void .ctor(int32 value) {
            ldarg.0
            call instance void Object::.ctor()
            ldarg.0
            ldarg.1
            stfld int32 Owner::Value
            ret
          }
        """ + "\n" + Method(kind, false) + "\n}";

    /// <summary>
    /// Selects the original instance or static method.
    /// </summary>
    /// <param name="kind">The observation exercised by the scenario.</param>
    /// <returns>The exact selected method signature.</returns>
    public static string Reference(string kind) => kind == "receiver"
        ? "instance int32 Owner::Read(int32&)" : "int32 Owner::Read(" + (kind == "inputs" ? "int32&" : "") + ")";

    /// <summary>
    /// Produces a method body whose revision changes observable execution.
    /// </summary>
    /// <param name="kind">The observation exercised by the scenario.</param>
    /// <param name="edited">Whether to produce the revised body.</param>
    /// <returns>The complete selected method declaration.</returns>
    public static string Method(string kind, bool edited)
    {
        if (kind == "exception")
        {
            return ".method public static int32 Read() {\nldstr \"" + (edited ? "edited" : "original")
                + " failure\"\nnewobj instance void InvalidOperationException::.ctor(string)\nthrow\n}";
        }

        if (kind == "inputs")
        {
            return ".method public static int32 Read(int32& value) {\nldarg.0\nldind.i4\nldarg.0\nldc.i4.0\nstind.i4\n"
                + (edited ? "ldc.i4.1\nadd\n" : "") + "ret\n}";
        }

        return """
            .method public instance int32 Read(int32& value) {
              ldarg.0
              dup
              ldfld int32 Owner::Value
              ldc.i4.1
              add
              stfld int32 Owner::Value
              ldarg.1
              dup
              ldind.i4
              ldc.i4.1
              add
              stind.i4
              ldarg.0
              ldfld int32 Owner::Value
              ldarg.1
              ldind.i4
              add
              ret
            }
            """.Replace("ldc.i4.1", edited ? "ldc.i4.2" : "ldc.i4.1", StringComparison.Ordinal);
    }

    /// <summary>
    /// Calls the copied alias while retaining each scenario's setup and exception handling.
    /// </summary>
    /// <param name="kind">The observation exercised by the scenario.</param>
    /// <returns>The complete parameterless scenario declaration.</returns>
    public static string Scenario(string kind) => kind switch
    {
        "exception" => """
            .method int32 Scenario() {
              .try {
                call Copy
                pop
                leave DONE
              } catch Exception {
                pop
                leave DONE
              }
              DONE: ldc.i4.0
              ret
            }
            """,
        "inputs" => """
            .method int32 Scenario() {
              .locals init (int32 value)
              ldc.i4.5
              stloc.0
              ldloca.s 0
              call Copy
              stloc.0
              ldloca.s 0
              call Copy
              pop
              ldc.i4.0
              ret
            }
            """,
        _ => """
            .method int32 Scenario() {
              .locals init (int32 value)
              ldc.i4.5
              stloc.0
              ldc.i4.7
              newobj instance void IlRepl.Edits.Copy.Owner::.ctor(int32)
              ldloca.s 0
              call Copy
              ret
            }
            """,
    };
}
