using IlRepl.Protocol;

namespace IlRepl.Host;

/// <summary>
/// Retains a source request's terminal operation state until its reply or a subsequent operation delivers it.
/// </summary>
internal sealed class HostCompletionDelivery
{
    /// <summary>
    /// The completed operation awaiting delivery within this request's own asynchronous execution context.
    /// </summary>
    internal ExecutionProgress? Pending { get; set; }
}
