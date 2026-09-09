using IlRepl.Engine.Binding;

namespace IlRepl.Repl;

/// <summary>
/// Warms only the immutable assembly sources of a snapshot captured under the session gate.
/// </summary>
internal static class CompletionWarmup
{
    /// <summary>
    /// Builds metadata indexes cooperatively and always releases the capture's assembly leases.
    /// </summary>
    /// <param name="snapshot">The immutable capture, whose ownership transfers to this task.</param>
    /// <param name="cancellationToken">Cancels warm-up after input or shutdown.</param>
    /// <returns>A task that settles after every lease is released.</returns>
    public static async Task RunAsync(BindingSnapshot snapshot, CancellationToken cancellationToken)
    {
        using (snapshot)
        {
            await Task.Yield();
            foreach (var source in snapshot.Catalog.Sources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await source.WarmIndexAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
