namespace IlRepl.Tests;

/// <summary>
/// Turns a value that may be missing into one that is present, and fails the test when it is not.
/// </summary>
/// <remarks>
/// The result is not nullable, so neither the compiler nor a code scanner has to trust an assertion made on an earlier line.
/// </remarks>
internal static class Required
{
    /// <summary>
    /// Returns the reference, or fails the test when there is none.
    /// </summary>
    /// <typeparam name="T">The type of the value.</typeparam>
    /// <param name="value">The value that may be missing.</param>
    /// <param name="what">What the value is, for the failure message.</param>
    /// <returns>The value.</returns>
    internal static T Value<T>(T? value, string what)
        where T : class => value ?? throw new AssertFailedException(what + " is missing.");

    /// <summary>
    /// Returns the value, or fails the test when there is none.
    /// </summary>
    /// <typeparam name="T">The type of the value.</typeparam>
    /// <param name="value">The value that may be missing.</param>
    /// <param name="what">What the value is, for the failure message.</param>
    /// <returns>The value.</returns>
    internal static T Value<T>(T? value, string what)
        where T : struct => value ?? throw new AssertFailedException(what + " is missing.");
}
