namespace IlRepl.Protocol;

/// <summary>
/// What a line of input amounts to once its comments are gone.
/// </summary>
public enum SourceLineKind
{
    /// <summary>
    /// Nothing but whitespace, outside any comment. At the top level this runs the cell.
    /// </summary>
    Blank,

    /// <summary>
    /// A comment and nothing else, or a blank line inside an open <c>/* */</c> comment. It is
    /// ignored wherever it appears.
    /// </summary>
    Comment,

    /// <summary>
    /// Something to parse: an instruction, a directive, or a command.
    /// </summary>
    Text,
}
