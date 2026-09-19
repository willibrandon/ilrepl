namespace IlRepl.Engine;

/// <summary>
/// The parsed invocation and explicit starting-state options of a comparison command.
/// </summary>
/// <param name="Name">The committed edit name.</param>
/// <param name="Arguments">The fixed argument literals, or empty for a scenario.</param>
/// <param name="Scenario">The parameterless session scenario, or null for a direct call.</param>
/// <param name="Assert">Whether incomplete comparisons and differences fail the submission.</param>
/// <param name="TimeoutMilliseconds">The execution time limit for each side after startup.</param>
/// <param name="StandardInput">The identical standard input supplied to both sides.</param>
/// <param name="FixtureDirectory">The directory whose contents initialize each separate working directory.</param>
internal sealed record ComparisonOptions(
    string Name,
    IReadOnlyList<string> Arguments,
    string? Scenario,
    bool Assert,
    int TimeoutMilliseconds,
    string StandardInput,
    string? FixtureDirectory);
