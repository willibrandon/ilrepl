namespace IlRepl.Protocol;

/// <summary>
/// Describes a host launch while keeping execution transport independent of its lifetime supervisor.
/// </summary>
/// <param name="Epoch">The supervisor generation accepting this request.</param>
/// <param name="Identity">The unique runtime scope identity.</param>
/// <param name="Executable">The dotnet executable to start.</param>
/// <param name="Arguments">The exact argument vector.</param>
/// <param name="WorkingDirectory">The host's initial directory.</param>
/// <param name="Environment">The complete child environment.</param>
public sealed record SupervisorLaunch(
    long Epoch,
    string Identity,
    string Executable,
    string[] Arguments,
    string WorkingDirectory,
    Dictionary<string, string?> Environment);
