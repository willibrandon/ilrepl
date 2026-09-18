namespace IlRepl.Tests.Engine;

/// <summary>
/// Records observable execution results independently of the conformance runner's own diagnostics.
/// </summary>
/// <param name="Result">The typed return value, including null.</param>
/// <param name="ExceptionType">The exact user exception type, or null on success.</param>
/// <param name="StandardOutput">Text written by user code to standard output.</param>
/// <param name="StandardError">Text written by user code to standard error.</param>
/// <param name="Runtime">The actual runtime version used by the child.</param>
/// <param name="Profile">The compilation profile supplied before startup.</param>
internal sealed record ExportObservation(ExportValue Result, string? ExceptionType, string StandardOutput,
    string StandardError, string Runtime, string Profile);
