using System.Globalization;

namespace IlRepl.Engine;

/// <summary>
/// Parses the duration syntax shared by behavioral comparisons and native inspection.
/// </summary>
internal static class ComparisonDuration
{
    /// <summary>
    /// Converts a positive duration in milliseconds, seconds, or minutes to the worker timeout.
    /// </summary>
    /// <param name="value">A duration such as 500ms, 30s, or 2m, defaulting to seconds.</param>
    /// <returns>The positive timeout in whole milliseconds.</returns>
    internal static int Parse(string value)
    {
        var factor = value.EndsWith("ms", StringComparison.Ordinal) ? 1 : value.EndsWith('m') ? 60000 : 1000;
        var number = value.EndsWith("ms", StringComparison.Ordinal) ? value[..^2]
            : value.EndsWith('s') || value.EndsWith('m') ? value[..^1] : value;
        if (!double.TryParse(number, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var duration)
            || !double.IsFinite(duration) || duration * factor is < 1 or > int.MaxValue)
            throw new ReplException("--timeout requires a positive duration, for example 500ms, 30s, or 2m");
        return (int)(duration * factor);
    }
}
