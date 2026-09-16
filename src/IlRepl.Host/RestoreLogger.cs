using System.Collections.Concurrent;
using NuGet.Common;

namespace IlRepl.Host;

/// <summary>
/// Retains NuGet diagnostics without writing into the host's RPC transport.
/// </summary>
internal sealed class RestoreLogger : LoggerBase
{
    private readonly ConcurrentQueue<string> _messages = new();

    /// <summary>
    /// Gets warnings and errors from the dependency restore.
    /// </summary>
    internal string[] Messages => [.. _messages];

    /// <summary>
    /// Starts a new restore pass without reporting diagnostics from a superseded discovery graph.
    /// </summary>
    internal void Clear() => _messages.Clear();

    /// <inheritdoc />
    public override void Log(ILogMessage message)
    {
        if (message.Level >= LogLevel.Warning)
        {
            _messages.Enqueue(message.Code != NuGetLogCode.Undefined ? message.Code + ": " + message.Message : message.Message);
        }
    }

    /// <inheritdoc />
    public override Task LogAsync(ILogMessage message)
    {
        Log(message);
        return Task.CompletedTask;
    }
}
