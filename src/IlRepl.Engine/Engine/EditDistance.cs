namespace IlRepl.Engine;

/// <summary>
/// Edit distance between names, for the did-you-mean a mistyped opcode, member, or type gets.
/// </summary>
public static class EditDistance
{
    /// <summary>
    /// The Levenshtein distance: the fewest single-character insertions, deletions, and substitutions that turn one string into the other.
    /// </summary>
    /// <param name="a">The first string.</param>
    /// <param name="b">The second string.</param>
    /// <returns>The distance.</returns>
    public static int Levenshtein(string a, string b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    /// <summary>
    /// True when the distance is at most <paramref name="bound"/>, decided without computing the
    /// whole table: a length difference beyond the bound settles it at once, and the rows are
    /// abandoned as soon as every cell in the band exceeds the bound.
    /// </summary>
    /// <param name="a">The first string.</param>
    /// <param name="b">The second string.</param>
    /// <param name="bound">The largest distance that counts.</param>
    /// <returns>True when the strings are within the bound.</returns>
    public static bool WithinBound(string a, string b, int bound)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (bound < 0)
        {
            return false;
        }

        if (Math.Abs(a.Length - b.Length) > bound)
        {
            return false;
        }

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            var low = Math.Max(1, i - bound);
            var high = Math.Min(b.Length, i + bound);
            current[0] = i;
            var best = current[0];
            for (var j = 1; j <= b.Length; j++)
            {
                if (j < low || j > high)
                {
                    current[j] = bound + 1;
                    continue;
                }

                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                best = Math.Min(best, current[j]);
            }

            if (best > bound)
            {
                return false;
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length] <= bound;
    }
}
