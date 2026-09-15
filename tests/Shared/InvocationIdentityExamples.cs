namespace IlRepl.Tests.Shared;

/// <summary>
/// Supplies real methods whose before and after graphs distinguish retained references from equal replacements.
/// </summary>
public static class InvocationIdentityExamples
{
    private const string TaskType = "class System.Threading.Tasks.Task`1<object>";
    private const string ValueTaskType = "valuetype System.Threading.Tasks.ValueTask`1<object>";
    private const string SourceType = "class System.Threading.Tasks.TaskCompletionSource`1<object>";
    private const string DictionaryType = "class System.Collections.Generic.Dictionary`2<string, object>";

    /// <summary>
    /// Declares actual payload constructors and an original method that retains or mutates its input.
    /// </summary>
    /// <param name="shape">The selected reference path, return, mutation, null, out or asynchronous case.</param>
    /// <returns>The complete original source.</returns>
    public static string Source(string shape)
    {
        var creation = shape switch
        {
            "ref-string" => "ldc.i4.s 120\nldc.i4.1\nnewobj instance void String::.ctor(char, int32)\n",
            "ref-box" => "ldc.i4.s 42\nbox int32\n",
            "throw" => "ldstr \"same exception\"\nnewobj instance void Exception::.ctor(string)\n",
            _ => "ldc.i4.s 42\nnewobj instance void Payload::.ctor(int32)\n",
        };
        return """
            .class public Payload {
              .field public int32 Number
              .method public instance void .ctor(int32 number) {
                ldarg.0
                call instance void Object::.ctor()
                ldarg.0
                ldarg.1
                stfld int32 Payload::Number
                ret
              }
            }
            .class public Owner {
              .field public object Value
            """ + "\n.method public static object NewValue() {\n" + creation + "ret\n}\n"
            + ".method public instance void .ctor() {\nldarg.0\ncall instance void Object::.ctor()\n"
            + "ldarg.0\ncall object Owner::NewValue()\nstfld object Owner::Value\nret\n}\n"
            + (shape is "task" or "valuetask" ? AsyncMembers() : "") + Method(shape, replace: false) + "\n}";
    }

    /// <summary>
    /// Replaces an equal object only when requested, with mutation, null and genuine out controls.
    /// </summary>
    /// <param name="shape">The selected fixture case.</param>
    /// <param name="replace">Whether to replace the original input reference.</param>
    /// <returns>The complete selected method.</returns>
    public static string Method(string shape, bool replace)
    {
        var header = shape switch
        {
            "receiver" => ".method public instance void Read()",
            "array" => ".method public static void Read(object[] values)",
            "dictionary" => ".method public static void Read(" + DictionaryType + " values)",
            "return" => ".method public static object Read(object value)",
            "mutate" or "throw" => ".method public static void Read(object value)",
            "out" => ".method public static void Read([out] object& value)",
            "task" => ".method public static " + TaskType + " Read(object value)",
            "valuetask" => ".method public static " + ValueTaskType + " Read(object value)",
            _ => ".method public static void Read(object& value)",
        };
        const string fresh = "call object Owner::NewValue()\n";
        var body = shape switch
        {
            "receiver" => replace ? "ldarg.0\n" + fresh + "stfld object Owner::Value\n" : "",
            "array" => replace ? "ldarg.0\nldc.i4.0\nldelem.ref\ncastclass object[]\nldc.i4.0\n" + fresh + "stelem.ref\n" : "",
            "dictionary" => replace ? "ldarg.0\nldstr \"item\"\n" + fresh
                + "callvirt instance void " + DictionaryType + "::set_Item(!0, !1)\n" : "",
            "return" => replace ? fresh : "ldarg.0\n",
            "mutate" => "ldarg.0\ncastclass Payload\nldc.i4.s 43\nstfld int32 Payload::Number\n",
            "throw" => (replace ? fresh : "ldarg.0\n") + "castclass Exception\nthrow\n",
            "null" => "",
            "out" => "ldarg.0\n" + fresh + "stind.ref\n",
            "task" or "valuetask" => "ldc.i4.s 64\nnewobj instance void " + SourceType
                + "::.ctor(valuetype System.Threading.Tasks.TaskCreationOptions)\nstsfld " + SourceType + " Owner::Pending\n"
                + (replace ? fresh : "ldarg.0\n") + "stsfld object Owner::Result\nldsfld " + SourceType + " Owner::Pending\n"
                + "callvirt instance class System.Threading.Tasks.Task`1<!0> " + SourceType + "::get_Task()\n"
                + (shape == "valuetask" ? "newobj instance void " + ValueTaskType
                    + "::.ctor(class System.Threading.Tasks.Task`1<!0>)\n" : ""),
            _ => replace ? "ldarg.0\n" + fresh + "stind.ref\n" : "",
        };
        var retained = "ldtoken method instance void Owner::.ctor()\npop\n";
        if (shape is "task" or "valuetask") retained += "ldtoken method void Owner::Complete()\npop\n";
        return header + " {\n" + retained + body + "ret\n}";
    }

    /// <summary>
    /// Returns a concrete reference to the original selected method.
    /// </summary>
    /// <param name="shape">The selected fixture case.</param>
    /// <returns>The exact source method reference.</returns>
    public static string Reference(string shape) => shape switch
    {
        "receiver" => "instance void Owner::Read()",
        "array" => "void Owner::Read(object[])",
        "dictionary" => "void Owner::Read(" + DictionaryType + ")",
        "return" => "object Owner::Read(object)",
        "mutate" or "throw" => "void Owner::Read(object)",
        "task" => TaskType + " Owner::Read(object)",
        "valuetask" => ValueTaskType + " Owner::Read(object)",
        _ => "void Owner::Read(object&)",
    };

    /// <summary>
    /// Builds a constant-result scenario and a separate actual ReferenceEquals witness over the selected call.
    /// </summary>
    /// <param name="shape">The selected fixture case.</param>
    /// <returns>The complete scenario and witness declarations.</returns>
    public static string Scenarios(string shape) => Scenario(shape, witness: false) + "\n" + Scenario(shape, witness: true);

    private static string Scenario(string shape, bool witness)
    {
        const string owner = "IlRepl.Edits.Copy.Owner";
        var locals = "object before, object after, class " + owner + " receiver, object[] values, " + DictionaryType
            + " dictionary, " + TaskType + " pending, " + ValueTaskType + " valueTask";
        var body = shape == "null" ? "ldnull\n" : "call object " + owner + "::NewValue()\n";
        body += "stloc.0\nldloc.0\nstloc.1\n";
        body += shape switch
        {
            "receiver" => "newobj instance void " + owner + "::.ctor()\nstloc.2\nldloc.2\nldloc.0\nstfld object "
                + owner + "::Value\nldloc.2\ncall Copy\nldloc.2\nldfld object " + owner + "::Value\nstloc.1\n",
            "array" => "ldc.i4.1\nnewarr object\ndup\nldc.i4.0\nldc.i4.1\nnewarr object\ndup\nldc.i4.0\nldloc.0\n"
                + "stelem.ref\nstelem.ref\nstloc.3\nldloc.3\ncall Copy\nldloc.3\nldc.i4.0\nldelem.ref\n"
                + "castclass object[]\nldc.i4.0\nldelem.ref\nstloc.1\n",
            "dictionary" => "newobj instance void " + DictionaryType + "::.ctor()\nstloc.s 4\nldloc.s 4\nldstr \"item\"\nldloc.0\n"
                + "callvirt instance void " + DictionaryType + "::Add(!0, !1)\nldloc.s 4\ncall Copy\nldloc.s 4\nldstr \"item\"\n"
                + "callvirt instance !1 " + DictionaryType + "::get_Item(!0)\nstloc.1\n",
            "return" => "ldloc.0\ncall Copy\nstloc.1\n",
            "mutate" => "ldloc.0\ncall Copy\n",
            "throw" => ".try {\nldloc.0\ncall Copy\nleave.s END\n} catch Exception {\nstloc.1\nleave.s END\n}\nEND:\n",
            "task" or "valuetask" => "ldloc.0\ncall Copy\n" + (shape == "task" ? "stloc.s 5\n" : "stloc.s 6\n")
                + "call void " + owner + "::Complete()\n"
                + (shape == "valuetask" ? "ldloca.s 6\ncall instance class System.Threading.Tasks.Task`1<!0> "
                    + ValueTaskType + "::AsTask()\nstloc.s 5\n" : "")
                + "ldloc.s 5\ncallvirt instance !0 " + TaskType + "::get_Result()\nstloc.1\n",
            _ => "ldloca.s 1\ncall Copy\n",
        };
        body += witness ? "ldloc.0\nldloc.1\ncall bool Object::ReferenceEquals(object, object)\nconv.i4\n" : "ldc.i4.s 42\n";
        return ".method int32 " + (witness ? "Witness" : "Scenario") + "() {\n.locals init (" + locals + ")\n" + body + "ret\n}";
    }

    private static string AsyncMembers() => ".field public static " + SourceType + " Pending\n.field public static object Result\n"
        + ".method public static void Complete() {\nldsfld " + SourceType + " Owner::Pending\nldsfld object Owner::Result\n"
        + "callvirt instance void " + SourceType + "::SetResult(!0)\nret\n}\n";
}
