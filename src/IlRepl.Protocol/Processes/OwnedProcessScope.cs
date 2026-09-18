namespace IlRepl.Protocol;

/// <summary>
/// Identifies an acknowledged process group without relying on a reusable numeric process identifier alone.
/// </summary>
/// <param name="Identity">The frontend's unique scope identity.</param>
/// <param name="ProcessId">The original process and Unix process group identifier.</param>
/// <param name="StartIdentity">The operating system start identity used to reject process identifier reuse.</param>
/// <param name="ParentIdentity">The owning runtime scope, or null for a runtime root.</param>
public sealed record OwnedProcessScope(string Identity, int ProcessId, long StartIdentity, string? ParentIdentity);
