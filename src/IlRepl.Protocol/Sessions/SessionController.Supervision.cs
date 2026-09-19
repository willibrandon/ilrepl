namespace IlRepl.Protocol;

/// <summary>
/// Keeps process ownership recovery distinct from execution runtime replacement.
/// </summary>
public sealed partial class SessionController : IProcessSupervision
{
    /// <inheritdoc />
    public ProcessSupervisionState Supervision => _engine is IProcessSupervision supervision
        ? supervision.Supervision : new ProcessSupervisionState(0, false, false, null);

    /// <inheritdoc />
    public event Action<ProcessSupervisionState>? SupervisionChanged;

    /// <inheritdoc />
    public Task RetrySupervisionAsync(CancellationToken cancellationToken) => _engine is IProcessSupervision supervision
        ? supervision.RetrySupervisionAsync(cancellationToken) : Task.CompletedTask;

    private void ObserveSupervision(IReplEngine engine)
    {
        if (engine is not IProcessSupervision supervision)
        {
            return;
        }

        supervision.SupervisionChanged += state =>
        {
            if (ReferenceEquals(engine, _engine))
            {
                SupervisionChanged?.Invoke(state);
            }
        };
    }
}
