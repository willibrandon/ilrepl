namespace IlRepl.Engine.Binding;

/// <summary>
/// What a <c>.clear</c> abandons.
/// </summary>
public enum ClearTarget
{
    /// <summary>
    /// The open method block.
    /// </summary>
    Method,

    /// <summary>
    /// The open class block.
    /// </summary>
    Type,

    /// <summary>
    /// The cell's body; declarations stay.
    /// </summary>
    Cell,
}
