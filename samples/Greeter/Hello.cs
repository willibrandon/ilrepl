namespace Greeter;

/// <summary>
/// Static members with the parameter shapes a cell is likely to call.
/// </summary>
public static class Hello
{
    /// <summary>
    /// The greeting prefix, so a cell can read a static field.
    /// </summary>
    public static readonly string Prefix = "Hello, ";

    /// <summary>
    /// A mutable static, so a cell can store into a static field.
    /// </summary>
    public static int Calls;

    /// <summary>
    /// Accepts a Cdecl pointer without invoking it, so completion can be tested on every runtime.
    /// </summary>
    /// <param name="function">The function pointer, which may be null.</param>
    /// <returns>Seven for a null pointer; eight otherwise.</returns>
    public static unsafe int AcceptCdecl(delegate* unmanaged[Cdecl]<int, int> function) => function == null ? 7 : 8;

    /// <summary>
    /// Greets by name.
    /// </summary>
    /// <param name="name">The name.</param>
    /// <returns>The greeting.</returns>
    public static string Say(string name)
    {
        Calls++;
        return Prefix + name + "!";
    }

    /// <summary>
    /// Adds two numbers.
    /// </summary>
    /// <param name="a">The first number.</param>
    /// <param name="b">The second number.</param>
    /// <returns>The sum.</returns>
    public static int Add(int a, int b) => a + b;

    /// <summary>
    /// A second method whose name differs from <see cref="Add(int, int)"/> only in case, as IL allows.
    /// </summary>
    /// <param name="a">The first number.</param>
    /// <param name="b">The second number.</param>
    /// <returns>The sum.</returns>
#pragma warning disable CA1707, IDE1006 // The lowercase name is the point: a member that differs from Add only in case.
    public static int add(int a, int b) => a + b;
#pragma warning restore CA1707, IDE1006

    /// <summary>
    /// Adds two numbers of a different type, to exercise overload resolution by parameter types.
    /// </summary>
    /// <param name="a">The first number.</param>
    /// <param name="b">The second number.</param>
    /// <returns>The sum.</returns>
    public static long Add(long a, long b) => a + b;

    /// <summary>
    /// Sums an array, to exercise <c>newarr</c> and <c>stelem</c>.
    /// </summary>
    /// <param name="values">The values.</param>
    /// <returns>The sum.</returns>
    public static long Sum(params int[] values)
    {
        long total = 0;
        foreach (var v in values)
        {
            total += v;
        }

        return total;
    }

    /// <summary>
    /// Returns its argument, to exercise generic method instantiation.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="value">The value.</param>
    /// <returns>The same value.</returns>
    public static T Echo<T>(T value) => value;

    /// <summary>
    /// Counts the arguments passed through the vararg convention. The runtime only supports this on Windows.
    /// </summary>
    /// <returns>The number of extra arguments.</returns>
    public static int CountArgs(__arglist)
    {
        var iterator = new ArgIterator(__arglist);
        return iterator.GetRemainingCount();
    }

    /// <summary>
    /// Calls the vararg method with one extra argument, so a listing of this body shows a call
    /// site with a sentinel and the optional argument types it carries.
    /// </summary>
    /// <returns>The count the callee reports.</returns>
    public static int CallCountArgs() => CountArgs(__arglist(123));

    /// <summary>
    /// Reads through a pointer, to exercise <c>ldloca</c>, <c>conv.u</c>, and pointer parameters.
    /// </summary>
    /// <param name="pointer">The address of an int.</param>
    /// <returns>The value at the address.</returns>
    public static unsafe int Deref(int* pointer) => *pointer;

    /// <summary>
    /// Writes to a byref, to exercise <c>ldloca</c> with reference parameters.
    /// </summary>
    /// <param name="target">The variable to set.</param>
    /// <param name="value">The value.</param>
    public static void Set(ref int target, int value) => target = value;
}
