namespace IlRepl.Engine.Binding;

/// <summary>
/// Describes the command syntax and structural transitions shared by submission and preview.
/// </summary>
public static class SessionTransitionRules
{
    private static readonly Dictionary<string, SessionTransition> Commands = new(StringComparer.Ordinal)
    {
        [".help"] = SessionTransition.None,
        [".h"] = SessionTransition.None,
        [".?"] = SessionTransition.None,
        [".quit"] = SessionTransition.Quit,
        [".exit"] = SessionTransition.Quit,
        [".q"] = SessionTransition.Quit,
        [".run"] = SessionTransition.Run,
        [".ops"] = SessionTransition.None,
        [".show"] = SessionTransition.None,
        [".list"] = SessionTransition.None,
        [".ls"] = SessionTransition.None,
        [".dis"] = SessionTransition.None,
        [".disassemble"] = SessionTransition.None,
        [".undo"] = SessionTransition.Undo,
        [".u"] = SessionTransition.Undo,
        [".clear"] = SessionTransition.Clear,
        [".reset"] = SessionTransition.Reset,
        [".types"] = SessionTransition.None,
        [".methods"] = SessionTransition.None,
        [".stack"] = SessionTransition.None,
        [".time"] = SessionTransition.None,
        [".quiet"] = SessionTransition.None,
        [".load"] = SessionTransition.Load,
        [".assemblies"] = SessionTransition.None,
        [".save"] = SessionTransition.Save,
        [".il"] = SessionTransition.None,
    };

    /// <summary>
    /// Every command word, aliases included.
    /// </summary>
    public static IReadOnlyCollection<string> Names => Commands.Keys;

    /// <summary>
    /// The structural effect of a command word.
    /// </summary>
    /// <param name="command">The word, with its dot.</param>
    /// <returns>The transition, or <see cref="SessionTransition.Unknown"/>.</returns>
    public static SessionTransition Of(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return Commands.TryGetValue(command, out var transition) ? transition : SessionTransition.Unknown;
    }

    /// <summary>
    /// Checks command recognition and required arguments before any structural effect or IO occurs.
    /// </summary>
    /// <param name="command">The command word.</param>
    /// <param name="argument">Its normalized argument.</param>
    public static void ValidateInput(string command, string? argument)
    {
        if (Of(command) == SessionTransition.Unknown)
        {
            throw new ReplException($"unknown command '{command}' (.help lists them)");
        }

        if (!string.IsNullOrEmpty(argument))
        {
            return;
        }

        var usage = command switch
        {
            ".load" => "usage: .load <assembly name | path.dll>",
            ".save" => "usage: .save <path.dll>",
            ".dis" or ".disassemble" => "usage: .dis <method reference>  "
                + "e.g. .dis instance string [System.Runtime]System.String::Trim()  or  .dis Fib",
            _ => null,
        };
        if (usage is not null)
        {
            throw new ReplException(usage);
        }
    }

    /// <summary>
    /// True when the command may run only with no block open: a run, or a save.
    /// </summary>
    /// <param name="transition">The transition.</param>
    /// <returns>True when an open block refuses it.</returns>
    public static bool RequiresNoOpenBlock(SessionTransition transition) => transition is SessionTransition.Run or SessionTransition.Save;

    /// <summary>
    /// What <c>.clear</c> takes away, given what is open: the method, else the class, else the cell's body.
    /// </summary>
    /// <param name="methodOpen">True while a method block is open.</param>
    /// <param name="typeOpen">True while a class block is open.</param>
    /// <returns>The clear target.</returns>
    public static ClearTarget ClearTargetOf(bool methodOpen, bool typeOpen) => methodOpen ? ClearTarget.Method : typeOpen ? ClearTarget.Type : ClearTarget.Cell;
}
