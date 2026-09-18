namespace IlRepl.Tests;

/// <summary>
/// Retains the exit status and independent output streams from an external conformance tool.
/// </summary>
/// <param name="ExitCode">The tool's process exit code.</param>
/// <param name="StandardOutput">The complete standard output.</param>
/// <param name="StandardError">The complete diagnostic output.</param>
internal sealed record ToolResult(int ExitCode, string StandardOutput, string StandardError);
