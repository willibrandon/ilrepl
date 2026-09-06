namespace Greeter;

/// <summary>
/// Applies delegates, for delegate construction tests.
/// </summary>
public static class Ops
{
    /// <summary>
    /// Invokes an operation.
    /// </summary>
    /// <param name="op">The operation.</param>
    /// <param name="a">The first operand.</param>
    /// <param name="b">The second operand.</param>
    /// <returns>The result.</returns>
    public static int Apply(IntOp op, int a, int b)
    {
        ArgumentNullException.ThrowIfNull(op);
        return op(a, b);
    }

    /// <summary>
    /// Multiplies, a method with the <see cref="IntOp"/> shape.
    /// </summary>
    /// <param name="a">The first operand.</param>
    /// <param name="b">The second operand.</param>
    /// <returns>The product.</returns>
    public static int Multiply(int a, int b) => a * b;
}
