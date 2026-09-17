namespace IlRepl.Protocol;

/// <summary>
/// The worker's selected runtime identity and latest durable inspection state.
/// </summary>
public sealed record NativeWorkerState
{
    /// <summary>
    /// The selected method descriptor, correlated with EventPipe MethodID.
    /// </summary>
    public ulong MethodId { get; init; }

    /// <summary>
    /// The method's original metadata identity, including closed generic arguments.
    /// </summary>
    public NativeMethodIdentity? Method { get; init; }

    /// <summary>
    /// The separately identified compilation-only address probes.
    /// </summary>
    public NativeProbe[] Probes { get; init; } = [];

    /// <summary>
    /// The evidence collected before the worker's latest execution boundary.
    /// </summary>
    public NativeReport Report { get; init; } = new();
}
