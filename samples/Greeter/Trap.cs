namespace Greeter;

/// <summary>
/// A type whose initializer throws, so anything that runs it by mistake is caught at once.
/// </summary>
public static class Trap
{
    /// <summary>
    /// How many times the initializer ran.
    /// </summary>
    public static int Touched;

#pragma warning disable CA1065 // The throwing initializer is the point of this type.
    static Trap()
    {
        Touched++;
        throw new InvalidOperationException("Trap's initializer ran; nothing that lists or completes members may run user code");
    }
#pragma warning restore CA1065

    /// <summary>
    /// A value that cannot be read without running the initializer.
    /// </summary>
    public static int Value => 1;
}
