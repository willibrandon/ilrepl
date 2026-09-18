namespace IlRepl.Protocol;

/// <summary>
/// Records process-local allocation and retained-memory evidence for explicitly requested reference measurements.
/// </summary>
/// <param name="Role">The frontend, host, or supervisor role.</param>
/// <param name="ProcessId">The operating-system process identity.</param>
/// <param name="Runtime">The actual runtime description.</param>
/// <param name="AllocatedBytes">Managed bytes allocated since process startup.</param>
/// <param name="RetainedBytes">Live managed heap bytes after a full collection at measurement shutdown.</param>
/// <param name="WorkingSetBytes">Resident process memory at measurement shutdown.</param>
/// <param name="Stages">Monotonic timestamps recorded during startup and execution.</param>
public sealed record ProcessMeasurement(string Role, int ProcessId, string Runtime, long AllocatedBytes,
    long RetainedBytes, long WorkingSetBytes, IReadOnlyDictionary<string, long> Stages);
