namespace IlRepl.Tests.Shared;

/// <summary>
/// Supplies representation-sensitive scenarios for the real desktop and browser comparison runtimes.
/// </summary>
public static class ValueTaskComparisonExamples
{
    /// <summary>
    /// Returns the selected value-task type.
    /// </summary>
    /// <param name="generic">Whether the value task returns an integer.</param>
    /// <returns>The complete CIL type name.</returns>
    public static string ValueTaskType(bool generic) => "valuetype System.Threading.Tasks.ValueTask" + (generic ? "`1<int32>" : "");

    /// <summary>
    /// Builds a method returning either a task-backed value task or an inline value.
    /// </summary>
    /// <param name="generic">Whether the value task returns an integer.</param>
    /// <param name="inline">Whether the result is represented inline.</param>
    /// <returns>The complete selected method.</returns>
    public static string Source(bool generic, bool inline)
    {
        var valueTask = ValueTaskType(generic);
        var task = "class System.Threading.Tasks.Task" + (generic ? "`1<int32>" : "");
        var body = !inline ? "ldc.i4.s 42\n"
            + "call class System.Threading.Tasks.Task`1<!!0> System.Threading.Tasks.Task::FromResult<int32>(!!0)\n"
            + "newobj instance void " + valueTask + "::.ctor(" + task + ")"
            : generic ? "ldc.i4.s 42\nnewobj instance void " + valueTask + "::.ctor(!0)"
                : ".locals init (" + valueTask + " value)\nldloc.0";
        return ".method public static " + valueTask + " Read() cil managed {\n" + body + "\nret\n}";
    }

    /// <summary>
    /// Distinguishes the returned value task's original backing task from an inline result.
    /// </summary>
    /// <param name="generic">Whether the value task returns an integer.</param>
    /// <returns>The complete scenario method.</returns>
    public static string Scenario(bool generic)
    {
        var valueTask = ValueTaskType(generic);
        var task = "class System.Threading.Tasks.Task" + (generic ? "`1<int32>" : "");
        var returned = generic ? "class System.Threading.Tasks.Task`1<!0>" : task;
        var asTask = "ldloca 0\ncall instance " + returned + " " + valueTask + "::AsTask()\n";
        return ".method bool Scenario() {\n.locals init (" + valueTask + " value)\ncall Copy\nstloc.0\n" + asTask
            + (generic ? asTask : "call class System.Threading.Tasks.Task System.Threading.Tasks.Task::get_CompletedTask()\n")
            + "call bool Object::ReferenceEquals(object, object)\nret\n}";
    }

    /// <summary>
    /// Creates a real pending channel wait and leaves its original pooled value task on the scenario's stack.
    /// </summary>
    /// <returns>The scenario locals and setup instructions.</returns>
    public static string PooledSetup()
    {
        const string channel = "class System.Threading.Channels.Channel`1<int32>";
        const string reader = "class System.Threading.Channels.ChannelReader`1<int32>";
        const string writer = "class System.Threading.Channels.ChannelWriter`1<int32>";
        const string token = "valuetype System.Threading.CancellationToken";
        const string valueTask = "valuetype System.Threading.Tasks.ValueTask`1<bool>";
        return ".locals init (" + channel + " channel, " + token
            + " token, " + valueTask + " result)\nldc.i4.1\n"
            + "newobj instance void System.Threading.Channels.BoundedChannelOptions::.ctor(int32)\n"
            + "call class System.Threading.Channels.Channel`1<!!0> System.Threading.Channels.Channel::CreateBounded<int32>("
            + "class System.Threading.Channels.BoundedChannelOptions)\nstloc.0\nldloc.0\ncallvirt instance " + reader + " "
            + channel + "::get_Reader()\nldloc.1\ncallvirt instance " + valueTask + " " + reader + "::WaitToReadAsync(" + token + ")\n"
            + "stloc.2\nldloc.0\ncallvirt instance " + writer + " " + channel + "::get_Writer()\nldc.i4.s 42\n"
            + "callvirt instance bool " + writer + "::TryWrite(!0)\npop\nldloc.2\n";
    }
}
