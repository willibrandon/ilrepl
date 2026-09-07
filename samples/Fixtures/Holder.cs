namespace Fixtures;

/// <summary>
/// Instance shapes: a volatile field, an auto-property with its backing field, and a constructor.
/// </summary>
public sealed class Holder
{
    /// <summary>
    /// A volatile field, whose references carry a required modifier.
    /// </summary>
    public volatile int Flag;

    /// <summary>
    /// Initializes the holder.
    /// </summary>
    /// <param name="name">The name.</param>
    public Holder(string name)
    {
        Name = name;
    }

    /// <summary>
    /// An auto-property, whose backing field has a made-up name.
    /// </summary>
    public string Name { get; set; }

    /// <summary>
    /// Reads the volatile field through the property.
    /// </summary>
    /// <returns>The flag plus the name's length.</returns>
    public int Read() => Flag + Name.Length;
}
