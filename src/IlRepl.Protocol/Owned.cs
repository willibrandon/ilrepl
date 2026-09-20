namespace IlRepl.Protocol;

/// <summary>
/// Holds a disposable value until its owner is known, and disposes it unless it was handed on.
/// </summary>
/// <remarks>
/// A method that creates a resource and returns it on success has to release it on every other way out, including an
/// exception nobody expected. With a using declaration for this holder that is one line, and <see cref="Release"/> marks
/// the single place where the caller takes over.
/// </remarks>
/// <typeparam name="T">The type of the held value.</typeparam>
/// <param name="value">The value to hold.</param>
public sealed class Owned<T>(T value) : IDisposable
    where T : class, IDisposable
{
    private T? _value = value;

    /// <summary>
    /// The held value.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The value was released or disposed.</exception>
    public T Value => _value ?? throw new ObjectDisposedException(nameof(Owned<T>));

    /// <summary>
    /// Hands the value to the caller, who disposes it from now on.
    /// </summary>
    /// <returns>The held value.</returns>
    public T Release()
    {
        var released = Value;
        _value = null;
        return released;
    }

    /// <summary>
    /// Disposes the value when it is still held.
    /// </summary>
    public void Dispose()
    {
        _value?.Dispose();
        _value = null;
    }
}
