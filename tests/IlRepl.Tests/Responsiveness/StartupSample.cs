namespace IlRepl.Tests.Responsiveness;

/// <summary>
/// Retains startup boundaries so prompt readiness, immediate editing, and host readiness can be compared directly.
/// </summary>
/// <param name="Launched">The timestamp immediately before starting the packaged process.</param>
/// <param name="Prompt">The first focused prompt, or null if startup stopped before it was observed.</param>
/// <param name="FirstEdit">The first successful editing frame, or null if that boundary was not observed.</param>
/// <param name="FirstAccepted">The first successful submission frame, or null if none was observed.</param>
/// <param name="HostReady">The host readiness acknowledgement, or null if the process did not record one.</param>
internal sealed record StartupSample(long Launched, long? Prompt, long? FirstEdit, long? FirstAccepted, long? HostReady);
