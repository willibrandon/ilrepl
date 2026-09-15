using IlRepl.Host;

if (args.Length > 0 && args[0] == "--comparison-worker")
{
    return await ComparisonWorkerProgram.RunAsync(args).ConfigureAwait(false);
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

return await HostServer.ServeStandardStreamsAsync(cancellation.Token).ConfigureAwait(false);
