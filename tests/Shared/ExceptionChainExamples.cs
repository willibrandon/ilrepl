namespace IlRepl.Tests.Shared;

/// <summary>
/// Supplies real nested exceptions and a cyclic chain that exceeds complete structural observation.
/// </summary>
public static class ExceptionChainExamples
{
    /// <summary>
    /// Throws identical outer exceptions whose deepest messages or observable chain differ.
    /// </summary>
    /// <param name="cycle">Whether the revision links the deepest exception back to the outer exception.</param>
    /// <param name="edited">Whether to produce the revised method body.</param>
    /// <param name="depth">Whether the revision adds a chain beyond the observation depth limit.</param>
    /// <returns>The complete method declaration.</returns>
    public static string Method(bool cycle, bool edited, bool depth = false)
    {
        var link = cycle && edited ? """
            ldtoken Exception
            call class Type Type::GetTypeFromHandle(valuetype RuntimeTypeHandle)
            ldstr "_innerException"
            ldc.i4.s 36
            callvirt instance class FieldInfo Type::GetField(string, valuetype BindingFlags)
            ldloc.0
            ldloc.1
            callvirt instance void FieldInfo::SetValue(object, object)
            """ : "";
        var chain = depth && edited ? "ldnull\n" + string.Concat(Enumerable.Repeat(
            "stloc.0\nldstr \"inner detail\"\nldloc.0\nnewobj instance void Exception::.ctor(string, class Exception)\n", 64))
            + "stloc.0\n" : "";
        var leaf = depth && edited ? "ldloc.0\nnewobj instance void ArgumentException::.ctor(string, class Exception)"
            : "newobj instance void ArgumentException::.ctor(string)";
        return ".method public static int32 Read() {\n.locals init (class Exception leaf, class Exception outer)\n" + chain + "ldstr \""
            + (edited ? "edited" : "original") + " detail\"\n" + leaf + "\nstloc.0\n"
            + "ldstr \"outer failure\"\nldstr \"middle failure\"\nldloc.0\n"
            + "newobj instance void InvalidOperationException::.ctor(string, class Exception)\n"
            + "newobj instance void Exception::.ctor(string, class Exception)\nstloc.1\n" + link + "\nldloc.1\nthrow\n}";
    }

    /// <summary>
    /// Runs the selected method with optional scenario-level handling of the complete exception chain.
    /// </summary>
    /// <param name="caught">Whether the scenario catches the method exception and returns normally.</param>
    /// <returns>The complete scenario declaration.</returns>
    public static string Scenario(bool caught) => caught ? """
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
        """ : ".method int32 Scenario() {\ncall Copy\nret\n}";
}
