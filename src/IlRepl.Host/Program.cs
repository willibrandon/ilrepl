using IlRepl.Host;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

return await HostServer.ServeStandardStreamsAsync(cancellation.Token).ConfigureAwait(false);
