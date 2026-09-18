using System.Collections.Concurrent;
using IlRepl.Protocol;

namespace IlRepl.Host;

/// <summary>
/// Registers worker groups independently of user execution and retains cleanup during supervisor adoption.
/// </summary>
public sealed partial class HostServer
{
    private readonly ConcurrentDictionary<string, OwnedProcessScope> _ownedWorkers = new();

    private static OwnedProcessScope ReadFrontendOwner()
    {
        var value = Environment.GetEnvironmentVariable("ILREPL_FRONTEND_OWNER")
            ?? throw new InvalidOperationException("the host has no frontend ownership identity");
        Environment.SetEnvironmentVariable("ILREPL_FRONTEND_OWNER", null);
        var fields = value.Split(':');
        if (fields.Length != 2 || !int.TryParse(fields[0], out var process) || !long.TryParse(fields[1], out var started))
            throw new InvalidDataException("invalid frontend ownership identity");
        return new OwnedProcessScope("frontend", process, started, null);
    }

    private async Task RegisterProcessAsync(OwnedProcessScope scope, CancellationToken cancellationToken)
    {
        _ownedWorkers[scope.Identity] = scope;
        if (_client is not null) await _client.RegisterProcessAsync(scope, cancellationToken).ConfigureAwait(false);
    }

    private async Task StopOwnedWorkersAsync()
    {
        var cleanups = _ownedWorkers.Values.Select(async scope =>
        {
            if (!OwnedProcessGroup.IsCurrent(scope)) return;
            using var group = new OwnedProcessGroup();
            group.Adopt(scope.ProcessId);
            await group.StopAsync().ConfigureAwait(false);
        });
        await Task.WhenAll(cleanups).ConfigureAwait(false);
    }
}
