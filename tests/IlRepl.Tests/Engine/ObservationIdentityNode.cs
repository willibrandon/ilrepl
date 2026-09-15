namespace IlRepl.Tests.Engine;

/// <summary>
/// Carries real mutable fields and reference edges for boundary observation tests.
/// </summary>
internal sealed class ObservationIdentityNode
{
    /// <summary>
    /// The scalar state read before and after mutation.
    /// </summary>
    internal int Number;

    /// <summary>
    /// The reference that may be retained, replaced or cyclic.
    /// </summary>
    internal object? Child;
}
