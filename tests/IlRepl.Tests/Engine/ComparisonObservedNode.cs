namespace IlRepl.Tests.Engine;

/// <summary>
/// A real object with shared fields and observable user-code effects in its computed and formatting members.
/// </summary>
internal sealed class ComparisonObservedNode
{
    private readonly int _secret = 42;

    /// <summary>
    /// The stored node name.
    /// </summary>
    public string Name = "node";

    /// <summary>
    /// The first graph edge.
    /// </summary>
    public ComparisonObservedNode? First;

    /// <summary>
    /// The second graph edge, which may alias another node.
    /// </summary>
    public ComparisonObservedNode? Second;

    /// <summary>
    /// The number of computed, equality, or formatting members invoked.
    /// </summary>
    public int UserCodeCalls;

    /// <summary>
    /// A computed property whose invocation changes observable state.
    /// </summary>
    public int Secret
    {
        get
        {
            UserCodeCalls++;
            return _secret;
        }
    }

    /// <summary>
    /// Formats the node while recording that user code ran.
    /// </summary>
    /// <returns>The node name.</returns>
    public override string ToString()
    {
        UserCodeCalls++;
        return Name;
    }

    /// <summary>
    /// Compares object identity while recording that user code ran.
    /// </summary>
    /// <param name="obj">The comparison object.</param>
    /// <returns>Whether the two references are identical.</returns>
    public override bool Equals(object? obj)
    {
        UserCodeCalls++;
        return ReferenceEquals(this, obj);
    }

    /// <summary>
    /// Supplies a deliberately shared hash while recording that user code ran.
    /// </summary>
    /// <returns>A fixed hash value.</returns>
    public override int GetHashCode()
    {
        UserCodeCalls++;
        return 0;
    }
}
