namespace IlRepl.Tests.Shared;

/// <summary>
/// Throws failures with identical messages and different stored exception details.
/// </summary>
public static class ExceptionStateExamples
{
    /// <summary>
    /// Declares the custom failure type and the selected throwing method.
    /// </summary>
    /// <param name="kind">The stored detail to exercise.</param>
    /// <param name="changed">Whether to use the edited detail.</param>
    /// <returns>The complete session source.</returns>
    public static string Source(string kind, bool changed) => """
        .class public Failure extends Exception {
          .field public string Detail
          .method public instance void .ctor(string detail) {
            ldarg.0
            ldstr "bad"
            call instance void Exception::.ctor(string)
            ldarg.0
            ldarg.1
            stfld string Failure::Detail
            ret
          }
        }
        """ + "\n" + Method(kind, changed);

    /// <summary>
    /// Builds a method that changes one stored field without changing the base exception message.
    /// </summary>
    /// <param name="kind">The stored detail to exercise.</param>
    /// <param name="changed">Whether to use the edited detail.</param>
    /// <returns>The complete throwing method.</returns>
    public static string Method(string kind, bool changed)
    {
        var detail = "ldstr \"" + (changed ? "right" : "left") + "\"\n";
        var body = kind switch
        {
            "parameter" => "ldstr \"bad\"\n" + detail + "newobj instance void ArgumentException::.ctor(string, string)\n",
            "actual" => "ldstr \"value\"\nldc.i4.s " + (changed ? "43" : "42") + "\nbox int32\nldstr \"bad\"\n"
                + "newobj instance void ArgumentOutOfRangeException::.ctor(string, object, string)\n",
            "custom" => detail + "newobj instance void Failure::.ctor(string)\n",
            "data" => "ldstr \"bad\"\nnewobj instance void Exception::.ctor(string)\ndup\n"
                + "callvirt instance class IDictionary Exception::get_Data()\nldstr \"detail\"\n" + detail
                + "callvirt instance void IDictionary::Add(object, object)\n",
            "help" or "source" => "ldstr \"bad\"\nnewobj instance void Exception::.ctor(string)\ndup\n" + detail
                + "callvirt instance void Exception::set_" + (kind == "help" ? "HelpLink" : "Source") + "(string)\n",
            _ => throw new ArgumentException("Unknown detail.", nameof(kind)),
        };
        return ".method public static int32 Read() {\n" + body + "throw\n}";
    }
}
