namespace IlRepl.Tests.Shared;

/// <summary>
/// Supplies executable exception ranges with configurable whitespace for desktop and browser editors.
/// </summary>
public static class ExceptionRangeExamples
{
    /// <summary>
    /// Builds a method whose exception path returns 42 through the requested handler kind.
    /// </summary>
    /// <param name="kind">The catch, filter, finally, or fault clause kind.</param>
    /// <param name="separator">The whitespace between range tokens.</param>
    /// <returns>The complete callable method declaration.</returns>
    public static string Source(string kind, string separator)
    {
        var unwind = kind is "finally" or "fault";
        var clause = unwind ? ".try TRY to UNWIND " + kind + " handler UNWIND to CATCH"
            : kind == "filter" ? ".try TRY to FILTER filter FILTER handler CATCH to DONE"
                : ".try TRY to CATCH catch Exception handler CATCH to DONE";
        var ranges = clause.Replace(" ", separator, StringComparison.Ordinal) + "\n";
        if (unwind)
        {
            ranges += ".try TRY to CATCH catch Exception handler CATCH to DONE".Replace(" ", separator, StringComparison.Ordinal)
                + "\n";
        }

        var handlers = unwind ? "UNWIND: ldloc.0\nldc.i4.2\nadd\nstloc.0\nendfinally\nCATCH: pop\nleave DONE\n"
            : (kind == "filter" ? "FILTER: pop\nldc.i4.1\nendfilter\n" : "")
                + "CATCH: pop\nldc.i4.s 42\nstloc.0\nleave DONE\n";
        return ".method public static int32 Work(int32 fail) cil managed {\n.locals init (int32 result)\n" + ranges
            + "TRY: ldc.i4.s 40\nstloc.0\nldarg.0\nbrfalse NORMAL\nnewobj instance void Exception::.ctor()\nthrow\n"
            + "NORMAL: leave DONE\n" + handlers + "DONE: ldloc.0\nret\n}";
    }
}
