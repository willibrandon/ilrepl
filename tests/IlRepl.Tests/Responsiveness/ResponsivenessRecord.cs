using IlRepl.Protocol;

namespace IlRepl.Tests.Responsiveness;

/// <summary>
/// Describes one reproducible packaged-process reference measurement without conflating budgets and observations.
/// </summary>
/// <param name="Schema">The measurement record format version.</param>
/// <param name="Commit">The repository revision measured, including whether source changes were present.</param>
/// <param name="Fixture">The generator version and content hashes.</param>
/// <param name="Scenario">The individual or combined workload.</param>
/// <param name="Sdk">The selected SDK version.</param>
/// <param name="Runtime">The measurement driver's runtime.</param>
/// <param name="OperatingSystem">The OS description.</param>
/// <param name="Rid">The process runtime identifier.</param>
/// <param name="Cpu">The machine's CPU identification.</param>
/// <param name="Machine">The reference machine identity.</param>
/// <param name="Configuration">The package build and measurement mode.</param>
/// <param name="Frontend">The absolute packaged frontend path.</param>
/// <param name="FrontendSha256">The exact Native AOT executable content hash.</param>
/// <param name="TimestampFrequency">Monotonic timestamp ticks per second for interpreting process stage evidence.</param>
/// <param name="Startups">Raw timestamps for each separately launched frontend and its first edit and submission.</param>
/// <param name="Metrics">Named startup and interaction distributions.</param>
/// <param name="Processes">Independent memory and runtime evidence for every observed process.</param>
internal sealed record ResponsivenessRecord(int Schema, string Commit, string Fixture, string Scenario, string Sdk,
    string Runtime, string OperatingSystem, string Rid, string Cpu, string Machine, string Configuration, string Frontend,
    string FrontendSha256, long TimestampFrequency, StartupSample[] Startups,
    IReadOnlyDictionary<string, LatencySamples> Metrics, ProcessMeasurement[] Processes);
