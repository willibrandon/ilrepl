using IlRepl.Protocol;

namespace IlRepl.Repl;

/// <summary>
/// Provisional script input for an edit, kept apart from committed session definitions.
/// </summary>
internal sealed class OpenEditBlock(string name)
{
    /// <summary>
    /// The prepared edit being submitted.
    /// </summary>
    internal string Name { get; } = name;

    /// <summary>
    /// The method source accepted so far, excluding the outer .edit braces.
    /// </summary>
    internal List<string> Lines { get; } = [];

    /// <summary>
    /// Original source coordinates corresponding to the accepted edit lines, when supplied by the editor.
    /// </summary>
    internal List<AnalysisLocation?> Locations { get; } = [];

    /// <summary>
    /// The open source braces, initially the outer .edit brace.
    /// </summary>
    internal int Depth { get; set; } = 1;
}
