using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;
using IlRepl.Engine;
using IlRepl.Protocol;

namespace IlRepl.Wasm;

/// <summary>
/// Delegates each comparison side to a fresh browser worker supervised by the page.
/// </summary>
internal static partial class BrowserComparisonRunner
{
    internal static async Task<ComparisonReply> RunAsync(ComparisonPackage package, CancellationToken cancellationToken)
    {
        var original = await RunSideAsync(package, true, cancellationToken).ConfigureAwait(false);
        var edited = await RunSideAsync(package, false, cancellationToken).ConfigureAwait(false);
        return ComparisonResults.Compare(package, original, edited);
    }

    private static async Task<ComparisonSide> RunSideAsync(ComparisonPackage package, bool original, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return new ComparisonSide("cancelled", [], null, null, "", "", "comparison cancelled");
        }

        var identity = Guid.NewGuid().ToString("N");
        var request = RunSide(identity, JsonSerializer.Serialize(package, ProtocolJsonContext.Default.ComparisonPackage), original);
        using var registration = cancellationToken.Register(() => CancelSide(identity));
        var response = await request.ConfigureAwait(false);
        return JsonSerializer.Deserialize(response, ProtocolJsonContext.Default.ComparisonSide)
            ?? throw new ReplException("the browser comparison worker returned no result");
    }

    [JSImport("runComparisonSide", "main.js")]
    [return: JSMarshalAs<JSType.Promise<JSType.String>>]
    private static partial Task<string> RunSide(string identity, string package, bool original);

    [JSImport("cancelComparisonSide", "main.js")]
    private static partial void CancelSide(string identity);
}
