namespace IlRepl.Tests.Responsiveness;

/// <summary>
/// Retains raw measured latencies with nearest-rank percentiles and explicit sample counts.
/// </summary>
/// <param name="Count">The number of measured operations.</param>
/// <param name="P50">The median in milliseconds.</param>
/// <param name="P95">The ninety-fifth percentile in milliseconds.</param>
/// <param name="P99">The ninety-ninth percentile in milliseconds.</param>
/// <param name="Milliseconds">Every raw latency in observation order.</param>
internal sealed record LatencySamples(int Count, double P50, double P95, double P99, double[] Milliseconds)
{
    /// <summary>
    /// Summarizes actual samples without interpolation or removal of slow observations.
    /// </summary>
    public static LatencySamples From(IEnumerable<double> samples)
    {
        var raw = samples.ToArray();
        var sorted = raw.Order().ToArray();
        double Rank(double percentile) => sorted.Length == 0 ? 0 : sorted[(int)Math.Ceiling(sorted.Length * percentile) - 1];
        return new LatencySamples(raw.Length, Rank(0.5), Rank(0.95), Rank(0.99), raw);
    }
}
