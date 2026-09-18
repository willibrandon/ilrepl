using System.Diagnostics;
using IlRepl.Protocol;

namespace IlRepl.Processes;

/// <summary>
/// Holds the direct host identity and diagnostic sink after ownership acknowledgement.
/// </summary>
/// <param name="Process">The host process, independent of its supervisor connection.</param>
/// <param name="Scope">The stable ownership identity.</param>
/// <param name="Diagnostics">The bounded inherited diagnostic pipe buffer.</param>
internal sealed record OwnedHostProcess(Process Process, OwnedProcessScope Scope, DiagnosticTail Diagnostics);
