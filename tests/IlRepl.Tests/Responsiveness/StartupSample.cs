namespace IlRepl.Tests.Responsiveness;

/// <summary>
/// Retains startup boundaries so prompt readiness, immediate editing, and host readiness can be compared directly.
/// </summary>
/// <param name="Launched">The timestamp immediately before starting the packaged process.</param>
/// <param name="Prompt">The first painted prompt with a focused editing caret.</param>
/// <param name="FirstEdit">The first frame showing an immediate real edit to that prompt.</param>
/// <param name="FirstAccepted">The first frame showing a successful submission.</param>
/// <param name="HostReady">The frontend's acknowledgement that its owned host is ready.</param>
internal sealed record StartupSample(long Launched, long Prompt, long FirstEdit, long FirstAccepted, long HostReady);
