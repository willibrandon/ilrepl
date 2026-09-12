namespace IlRepl.Docs.Tests;

public sealed partial class LiveSessionTests
{
    /// <summary>
    /// Browser Mono runs the verifier corpus once and survives a thousand generic edits in both browser shells.
    /// </summary>
    [TestMethod]
    [DoNotParallelize]
    [Timeout(1_200_000, CooperativeCancellation = true)]
    public async Task BrowserStress_ControlFlowCorpusAndGenericEditingStayBounded()
    {
        // Chromium and WebKit host the same published Mono runtime. Focused control-flow tests exercise
        // both browser shells, while the exhaustive engine corpus needs to run once against that runtime.
        await Task.WhenAll(
            RunControlFlowCorpusAsync("chromium"),
            RunThousandGenericEditsAsync("chromium"),
            RunThousandGenericEditsAsync("webkit"));
    }
}
