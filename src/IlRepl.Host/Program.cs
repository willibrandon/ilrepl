using IlRepl.Host;
using IlRepl.Protocol;

using var measurements = new ProcessMeasurements("host");

if (args.Length > 0 && args[0] == "--native-worker")
{
    return await NativeWorkerProgram.RunAsync(args).ConfigureAwait(false);
}

if (args.Length > 0 && args[0] == "--comparison-worker")
{
    return await ComparisonWorkerProgram.RunAsync(args).ConfigureAwait(false);
}

OwnedProcessGroup.PrepareWorker();

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    // Lifecycle requests arrive through the frontend's direct control connection.
};

if (args.Length != 2 || args[0] != "--socket")
{
    Console.Error.WriteLine("ilrepl-host must be started by the ilrepl frontend");
    return 64;
}
var secret = Environment.GetEnvironmentVariable("ILREPL_HOST_HANDSHAKE")
    ?? throw new InvalidOperationException("the host connection has no launch identity");
Environment.SetEnvironmentVariable("ILREPL_HOST_HANDSHAKE", null);
return await HostServer.ServeSocketAsync(args[1], secret, cancellation.Token).ConfigureAwait(false);
