using System.Globalization;

namespace IlRepl.Engine;

/// <summary>
/// Presents native instruction changes as unified hunks using the same sequence alignment as IL differences.
/// </summary>
public static class NativeDifference
{
    /// <summary>
    /// Aligns complete identity-preserving instructions and retains three unchanged lines around each change.
    /// </summary>
    /// <param name="left">The original or left instruction sequence.</param>
    /// <param name="right">The edited or right instruction sequence.</param>
    /// <param name="leftName">The original or left label.</param>
    /// <param name="rightName">The edited or right label.</param>
    /// <returns>Unified file headers, hunk ranges, and changed instructions, or no lines when equal.</returns>
    public static string[] Create(IReadOnlyList<string> left, IReadOnlyList<string> right, string leftName, string rightName)
    {
        var aligned = SequenceDiff.Match(left, right);
        var changes = Enumerable.Range(0, aligned.Count).Where(index =>
            aligned[index].Original < 0 || aligned[index].Edited < 0).ToArray();
        if (changes.Length == 0)
        {
            return [];
        }

        var result = new List<string> { "--- " + leftName, "+++ " + rightName };
        var leftPosition = new int[aligned.Count + 1];
        var rightPosition = new int[aligned.Count + 1];
        for (var index = 0; index < aligned.Count; index++)
        {
            leftPosition[index + 1] = leftPosition[index] + (aligned[index].Original < 0 ? 0 : 1);
            rightPosition[index + 1] = rightPosition[index] + (aligned[index].Edited < 0 ? 0 : 1);
        }

        for (var change = 0; change < changes.Length; change++)
        {
            var start = Math.Max(0, changes[change] - 3);
            var end = Math.Min(aligned.Count, changes[change] + 4);
            while (change + 1 < changes.Length && changes[change + 1] - 3 <= end)
            {
                end = Math.Min(aligned.Count, changes[++change] + 4);
            }

            result.Add("@@ -" + Range(leftPosition[start], leftPosition[end] - leftPosition[start])
                + " +" + Range(rightPosition[start], rightPosition[end] - rightPosition[start]) + " @@");
            for (var index = start; index < end; index++)
            {
                var (original, edited) = aligned[index];
                result.Add(original < 0 ? "+" + right[edited] : edited < 0 ? "-" + left[original] : " " + left[original]);
            }
        }

        return [.. result];
    }

    private static string Range(int position, int count) => (count == 0 ? position : position + 1)
        .ToString(CultureInfo.InvariantCulture) + "," + count.ToString(CultureInfo.InvariantCulture);
}
