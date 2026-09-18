namespace IlRepl.Protocol;

/// <summary>
/// Records observed process termination without confusing it with an ordinary source diagnostic.
/// </summary>
/// <param name="ProcessId">The process that ended.</param>
/// <param name="ExitCode">The observed native exit code, when available.</param>
/// <param name="StandardError">The retained tail of process diagnostics.</param>
/// <param name="Expected">Whether the frontend deliberately stopped the process.</param>
public sealed record HostExit(int ProcessId, int? ExitCode, string StandardError, bool Expected);
