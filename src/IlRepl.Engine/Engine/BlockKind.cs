namespace IlRepl.Engine;

/// <summary>
/// A structured exception-handling boundary inside a cell, mirroring the
/// <c>ILGenerator</c> block methods.
/// </summary>
public enum BlockKind
{
    /// <summary>
    /// Opens a protected region (<c>.try {</c>).
    /// </summary>
    Try,

    /// <summary>
    /// Begins a typed catch handler (<c>} catch T {</c>).
    /// </summary>
    Catch,

    /// <summary>
    /// Begins a filter expression (<c>} filter {</c>), which must end with <c>endfilter</c>.
    /// </summary>
    Filter,

    /// <summary>
    /// Begins the handler that follows a filter (<c>} handler {</c>).
    /// </summary>
    FilterHandler,

    /// <summary>
    /// Begins a finally handler (<c>} finally {</c>).
    /// </summary>
    Finally,

    /// <summary>
    /// Begins a fault handler (<c>} fault {</c>).
    /// </summary>
    Fault,

    /// <summary>
    /// Closes the whole protected region (<c>}</c>).
    /// </summary>
    End,
}
