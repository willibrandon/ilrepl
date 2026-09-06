namespace Greeter;

/// <summary>
/// An instance type with fields, constructors, a property, and a virtual method.
/// </summary>
public class Counter
{
    /// <summary>
    /// How many counters have been created.
    /// </summary>
    public static int Instances;

    /// <summary>
    /// The current count.
    /// </summary>
    public int Count;

    /// <summary>
    /// Creates a counter at zero.
    /// </summary>
    public Counter()
    {
        Instances++;
    }

    /// <summary>
    /// Creates a counter at a starting value.
    /// </summary>
    /// <param name="start">The starting value.</param>
    public Counter(int start) : this()
    {
        Count = start;
    }

    /// <summary>
    /// The current count, as a property.
    /// </summary>
    public int Value => Count;

    /// <summary>
    /// Adds one.
    /// </summary>
    public void Increment() => Count++;

    /// <summary>
    /// Adds an amount and returns the counter for chaining.
    /// </summary>
    /// <param name="amount">The amount.</param>
    /// <returns>This counter.</returns>
    public Counter Add(int amount)
    {
        Count += amount;
        return this;
    }

    /// <inheritdoc />
    public override string ToString() => $"Counter({Count})";
}
