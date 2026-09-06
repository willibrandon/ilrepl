namespace Greeter;

/// <summary>
/// A delegate over two integers, for <c>ldftn</c> and <c>newobj</c> delegate construction.
/// </summary>
/// <param name="a">The first operand.</param>
/// <param name="b">The second operand.</param>
/// <returns>The result.</returns>
public delegate int IntOp(int a, int b);
