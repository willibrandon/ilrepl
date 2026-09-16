namespace IlRepl.Engine;

/// <summary>
/// Carries a session's dependency resolver through nested definition compilation without sharing it across sessions.
/// </summary>
internal sealed class ReferenceLoadScope : IDisposable
{
    private static readonly AsyncLocal<ReferenceLoadContext?> Active = new();
    private readonly ReferenceLoadContext? _previous;

    /// <summary>
    /// The resolver captured by a newly created definition load context.
    /// </summary>
    internal static ReferenceLoadContext? Current => Active.Value;

    /// <summary>
    /// Installs the owning session's resolver for the duration of one nested operation.
    /// </summary>
    /// <param name="context">The session dependency context.</param>
    internal ReferenceLoadScope(ReferenceLoadContext context)
    {
        _previous = Active.Value;
        Active.Value = context;
    }

    /// <inheritdoc />
    public void Dispose() => Active.Value = _previous;
}
