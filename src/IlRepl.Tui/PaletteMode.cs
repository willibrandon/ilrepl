namespace IlRepl.Tui;

/// <summary>
/// Describes whether completion is closed, requested, visible, dismissed or waiting for an explicit retry.
/// </summary>
public enum PaletteMode
{
    /// <summary>
    /// No query is active at the current site.
    /// </summary>
    Closed,

    /// <summary>
    /// A query is pending and has no selectable rows yet.
    /// </summary>
    Requested,

    /// <summary>
    /// Current rows are available, possibly while another page is pending.
    /// </summary>
    Open,

    /// <summary>
    /// Escape or acceptance dismissed this document and caret position.
    /// </summary>
    Dismissed,

    /// <summary>
    /// A request failed and will retry after an edit or an explicit Tab.
    /// </summary>
    Faulted,
}
