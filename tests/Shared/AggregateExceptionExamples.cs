namespace IlRepl.Tests.Shared;

/// <summary>
/// Real IL throws aggregates whose later children vary while the outer message and first child stay unchanged.
/// </summary>
public static class AggregateExceptionExamples
{
    /// <summary>
    /// Creates a later aggregate child whose inner chain exceeds the observation depth limit.
    /// </summary>
    /// <returns>The complete throwing method declaration.</returns>
    public static string DeepMethod()
    {
        return Method(false, false)
            .Replace("Read() {", "Read() {\n.locals init (class Exception leaf, int32 depth)", StringComparison.Ordinal)
            .Replace("newobj instance void ArgumentException::.ctor(string)", """
                newobj instance void ArgumentException::.ctor(string)
                stloc.0
                ldc.i4.0
                stloc.1
                deepen:
                ldstr "deeper failure "
                ldloca.s 1
                call instance string int32::ToString()
                call string string::Concat(string, string)
                ldloc.0
                newobj instance void Exception::.ctor(string, class Exception)
                stloc.0
                ldloc.1
                ldc.i4.1
                add
                stloc.1
                ldloc.1
                ldc.i4.s 80
                blt deepen
                ldloc.0
                """, StringComparison.Ordinal);
    }

    /// <summary>
    /// Creates a method with two aggregate children, optionally nesting the second child in another aggregate.
    /// </summary>
    /// <param name="changed">Whether the later child differs from the original.</param>
    /// <param name="nested">Whether the later child is nested in an aggregate.</param>
    /// <returns>The complete method declaration.</returns>
    public static string Method(bool changed, bool nested) => """
        .method public static int32 Read() {
          ldstr "outer failure"
          ldc.i4.2
          newarr Exception
          dup
          ldc.i4.0
          ldstr "first failure"
          newobj instance void InvalidOperationException::.ctor(string)
          stelem.ref
          dup
          ldc.i4.1
        """ + "\n" + (nested ? """
          ldstr "nested failure"
          ldc.i4.2
          newarr Exception
          dup
          ldc.i4.0
          ldstr "nested first failure"
          newobj instance void InvalidOperationException::.ctor(string)
          stelem.ref
          dup
          ldc.i4.1
        """ + "\n" : "") + "ldstr \"" + (changed ? "edited detail" : "original detail") + "\"\n" + """
          newobj instance void ArgumentException::.ctor(string)
          stelem.ref
        """ + "\n" + (nested ? "newobj instance void AggregateException::.ctor(string, class Exception[])\nstelem.ref\n" : "") + """
          newobj instance void AggregateException::.ctor(string, class Exception[])
          throw
        }
        """;
}
