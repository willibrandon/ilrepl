namespace IlRepl.Protocol;

/// <summary>
/// A prepared comparison whose starting conditions can be displayed before its workers start.
/// </summary>
/// <param name="Identity">The one-use identity of the captured package held by the engine.</param>
/// <param name="Name">The edit name.</param>
/// <param name="StartingState">The starting conditions to display before execution.</param>
public sealed record ComparisonTicket(string Identity, string Name, string StartingState);
