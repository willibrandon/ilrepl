using System.Runtime.CompilerServices;

namespace Fixtures;

/// <summary>
/// Method shapes the C# compiler produces, for reading back with <c>.dis</c>.
/// </summary>
public static class Shapes
{
    /// <summary>
    /// A lambda, which the compiler moves into a nested <c>&lt;&gt;c</c> class with a made-up name.
    /// </summary>
    /// <param name="values">The values.</param>
    /// <returns>The doubled values.</returns>
    public static IEnumerable<int> Doubled(IEnumerable<int> values) => values.Select(v => v * 2);

    /// <summary>
    /// A local function, which becomes a method with a made-up name.
    /// </summary>
    /// <param name="n">The count.</param>
    /// <returns>The sum of 1 to <paramref name="n"/>.</returns>
    public static int Sum(int n)
    {
        return Add(0, 1);

        int Add(int total, int i) => i > n ? total : Add(total + i, i + 1);
    }

    /// <summary>
    /// An async method, whose body only starts a state machine.
    /// </summary>
    /// <returns>Forty-two, later.</returns>
    public static async Task<int> Later()
    {
        await Task.Yield();
        return 42;
    }

    /// <summary>
    /// A body compiled without zeroed locals, so its header lacks the init bit.
    /// </summary>
    /// <returns>Zero.</returns>
    [SkipLocalsInit]
    public static unsafe int NoInit()
    {
        var buffer = stackalloc int[4];
        buffer[0] = 0;
        return buffer[0];
    }

    /// <summary>
    /// A function pointer parameter, invoked through calli.
    /// </summary>
    /// <param name="f">The function.</param>
    /// <param name="x">Its argument.</param>
    /// <returns>The result.</returns>
    public static unsafe int Apply(delegate*<int, int> f, int x) => f(x);

    /// <summary>
    /// An exception filter.
    /// </summary>
    /// <param name="d">The divisor.</param>
    /// <returns>The quotient, or 42 on division by zero.</returns>
    public static int Safe(int d)
    {
        try
        {
            return 1 / d;
        }
        catch (DivideByZeroException e) when (e.Message.Length > 0)
        {
            return 42;
        }
    }

    /// <summary>
    /// A try, a catch, and a finally: the shape whose finally protects the catch as well.
    /// </summary>
    /// <param name="fail">True to take the exception path.</param>
    /// <returns>Eleven when the exception path runs.</returns>
    public static int Guarded(bool fail)
    {
        var x = 0;
        try
        {
            if (fail)
            {
                throw new InvalidOperationException("boom");
            }

            x = 1;
        }
        catch (InvalidOperationException)
        {
            x = 10;
        }
        finally
        {
            x += 1;
        }

        return x;
    }

    /// <summary>
    /// A switch with a jump table.
    /// </summary>
    /// <param name="n">The selector.</param>
    /// <returns>A word.</returns>
    public static string Word(int n) => n switch
    {
        0 => "zero",
        1 => "one",
        2 => "two",
        3 => "three",
        4 => "four",
        _ => "many",
    };

    /// <summary>
    /// A method whose name ILAsm reads as an opcode unless it is quoted.
    /// </summary>
    /// <param name="a">One.</param>
    /// <param name="b">Two.</param>
    /// <returns>Their sum.</returns>
    public static int add(int a, int b) => a + b;

    /// <summary>
    /// Floating point constants of both widths.
    /// </summary>
    /// <returns>A product.</returns>
    public static double Floats() => 1.5f * -0.25;

    /// <summary>
    /// A generic method with a constraint.
    /// </summary>
    /// <typeparam name="T">A comparable type.</typeparam>
    /// <param name="a">One.</param>
    /// <param name="b">Two.</param>
    /// <returns>The larger.</returns>
    public static T Larger<T>(T a, T b)
        where T : IComparable<T> => a.CompareTo(b) >= 0 ? a : b;

    /// <summary>
    /// A token for an open generic type, which is the definition and not an instantiation.
    /// </summary>
    /// <returns>The open list type.</returns>
    public static Type Open() => typeof(List<>);

    /// <summary>
    /// Constants that are not finite, which have no decimal spelling.
    /// </summary>
    /// <returns>A tuple of a NaN and an infinity.</returns>
    public static (float, double) NonFinite() => (float.NaN, double.PositiveInfinity);

    /// <summary>
    /// A lambda over a generic method's type parameter, which the compiler moves into a generic closure class.
    /// </summary>
    /// <typeparam name="T">The captured type.</typeparam>
    /// <param name="value">The value to capture.</param>
    /// <returns>A function returning the value.</returns>
    public static Func<T> Capture<T>(T value) => () => value;
}
