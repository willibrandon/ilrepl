namespace IlRepl.Engine;

/// <summary>
/// Finds a name that nothing else has yet, by adding underscores to a wanted one.
/// </summary>
internal static class UniqueName
{
    /// <summary>
    /// Returns the wanted name, followed by as many underscores as it takes to be free.
    /// </summary>
    /// <param name="wanted">The name to start from.</param>
    /// <param name="isTaken">Whether a candidate name is already in use.</param>
    /// <returns>The first free name.</returns>
    internal static string From(string wanted, Func<string, bool> isTaken)
    {
        var underscores = 0;
        while (isTaken(Padded(wanted, underscores)))
        {
            underscores++;
        }

        return Padded(wanted, underscores);
    }

    private static string Padded(string name, int underscores) => name.PadRight(name.Length + underscores, '_');
}
