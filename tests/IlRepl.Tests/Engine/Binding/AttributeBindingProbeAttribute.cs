namespace IlRepl.Tests.Engine.Binding;

/// <summary>
/// Records any accidental execution while attribute metadata is bound.
/// </summary>
public sealed class AttributeBindingProbeAttribute : Attribute
{
    /// <summary>
    /// The number of constructor and setter calls made by a test.
    /// </summary>
    public static int Calls;

    /// <summary>
    /// Initializes an attribute and records the invocation.
    /// </summary>
    /// <param name="values">The attribute's array argument.</param>
    public AttributeBindingProbeAttribute(int[] values)
    {
        Calls += values.Length;
    }

    /// <summary>
    /// A named argument whose setter records an invocation.
    /// </summary>
    public string Label
    {
        get;
        set
        {
            field = value;
            Calls += value.Length;
        }
    } = "";
}
