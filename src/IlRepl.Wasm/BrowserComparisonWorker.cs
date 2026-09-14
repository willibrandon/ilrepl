using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;
using IlRepl.Engine;
using IlRepl.Protocol;

namespace IlRepl.Wasm;

/// <summary>
/// Exposes a single comparison execution in a browser runtime that never starts a REPL session.
/// </summary>
public static partial class BrowserComparisonWorker
{
    /// <summary>
    /// Runs the supplied side after the page creates its dedicated worker and runtime.
    /// </summary>
    /// <param name="json">The frozen comparison package.</param>
    /// <param name="original">Whether to execute the captured original.</param>
    /// <returns>The serialized observations for the parent supervisor.</returns>
    [JSExport]
    [return: JSMarshalAs<JSType.Promise<JSType.String>>]
    public static async Task<string> RunAsync(string json, bool original)
    {
        await JSHost.ImportAsync("comparison.js", "../comparison-interop.js").ConfigureAwait(false);
        var package = JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.ComparisonPackage)
            ?? throw new ReplException("the comparison package is missing");
        var result = await ComparisonWorker.ExecuteAsync(package, original, Ready, OutputLimit, captureOutput: false).ConfigureAwait(false);
        return JsonSerializer.Serialize(result, ProtocolJsonContext.Default.ComparisonSide);
    }

    [JSImport("ready", "comparison.js")]
    private static partial void Ready();

    [JSImport("outputLimit", "comparison.js")]
    private static partial void OutputLimit();
}
