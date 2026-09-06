namespace IlRepl.Tests;

/// <summary>
/// Marks a test inconclusive when a runtime capability is missing.
/// </summary>
internal static class TestSkip
{
    /// <summary>
    /// Marks the test inconclusive unless the condition holds.
    /// </summary>
    /// <param name="condition">Whether the test can continue.</param>
    /// <param name="message">The reason for skipping.</param>
    public static void Unless(bool condition, string message)
    {
        if (!condition)
        {
            Assert.Inconclusive(message);
        }
    }
}
