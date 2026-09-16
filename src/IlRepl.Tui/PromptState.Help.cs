namespace IlRepl.Tui;

/// <summary>
/// Holds editor state independently of the currently visible prompt or contextual help.
/// </summary>
public sealed partial class PromptState
{
    /// <summary>
    /// The expanded help view, or null while editing.
    /// </summary>
    public PromptHelp? Help { get; set; }

    /// <summary>
    /// Opens documentation using the host's browser mechanism.
    /// </summary>
    public Action<string>? OpenDocumentation { get; set; }

    /// <summary>
    /// Publishes the selected documentation URL to hosts that require activation on their UI thread.
    /// </summary>
    public Action<string?, int, bool>? DocumentationTargetChanged { get; set; }

    /// <summary>
    /// Counts help keys after their actions complete so browser links cannot acknowledge queued input early.
    /// </summary>
    internal int HelpInputSequence { get; set; }

    /// <summary>
    /// Reads current host identity when an action arrives between rendered frames.
    /// </summary>
    internal Func<(long Revision, long Assemblies)>? CurrentHelpIdentity { get; set; }

    /// <summary>
    /// The current engine revision used to reject stale help actions.
    /// </summary>
    internal long HelpRevision { get; set; }

    /// <summary>
    /// The current assembly version used to reject stale help actions.
    /// </summary>
    internal long HelpAssemblyVersion { get; set; }
}
