namespace IlRepl.Tui;

/// <summary>
/// One unit of a submission: the buffer lines it spans, and which of them go to the engine. A
/// unit is rolled back and returned to the editor whole when a line of it is refused.
/// </summary>
/// <param name="Start">The first buffer line of the unit.</param>
/// <param name="End">The line after the unit's last.</param>
/// <param name="Sends">The buffer lines sent to the engine, in order: blank lines inside a block are left out.</param>
/// <param name="Kind">What the unit is.</param>
public sealed record SubmissionUnit(int Start, int End, IReadOnlyList<int> Sends, SubmissionUnitKind Kind)
{
    /// <summary>
    /// Two units are equal when they span the same lines, send the same lines, and are the same kind.
    /// </summary>
    /// <param name="other">The other unit.</param>
    /// <returns>True when equal.</returns>
    public bool Equals(SubmissionUnit? other) =>
        other is not null && Start == other.Start && End == other.End && Kind == other.Kind && Sends.SequenceEqual(other.Sends);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Start);
        hash.Add(End);
        hash.Add(Kind);
        foreach (var send in Sends)
        {
            hash.Add(send);
        }

        return hash.ToHashCode();
    }
}
