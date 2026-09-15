using IlRepl.Tests.Shared;
using Microsoft.Playwright;

namespace IlRepl.Docs.Tests;

/// <summary>
/// Browser comparison reports expose the inputs and caught exceptions observed by real workers.
/// </summary>
public sealed partial class LiveSessionTests
{
    /// <summary>
    /// Receiver and argument changes and caught method failures remain visible in the live transcript.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="kind">The observation that distinguishes the executions.</param>
    [TestMethod]
    [DataRow("chromium", "receiver")]
    [DataRow("webkit", "receiver")]
    [DataRow("chromium", "inputs")]
    [DataRow("webkit", "inputs")]
    [DataRow("chromium", "exception")]
    [DataRow("webkit", "exception")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_ComparisonShowsInputsAndCaughtExceptions(string browser, string kind)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        await SubmitEditSourceAsync(page, ComparisonTranscriptExamples.Source(kind) + "\n.edit "
            + ComparisonTranscriptExamples.Reference(kind) + " as Copy {\n" + ComparisonTranscriptExamples.Method(kind, true)
            + "\n}\n" + ComparisonTranscriptExamples.Scenario(kind), "end of method Scenario");
        await TypeLineAsync(page, ".compare Copy using Scenario");
        await ExpectComparisonTextAsync(page, "edited: completed");
        var text = await ReadComparisonTranscriptAsync(page);
        Assert.Contains("Copy: " + (kind == "inputs" ? "different-inputs" : "different"), text);
        const string scalar = "[System.Private.CoreLib]System.Int32";
        const string failure = "call 1 threw [System.Private.CoreLib]System.InvalidOperationException: ";
        if (kind == "exception")
        {
            Assert.Contains(failure + "original failure", text);
            Assert.Contains(failure + "edited failure", text);
            Assert.Contains("HRESULT 0x80131509", text);
        }
        else if (kind == "inputs")
        {
            Assert.Contains($"call 2 argument 0: {scalar} \"5\" -> {scalar} \"0\"", text);
            Assert.Contains($"call 2 argument 0: {scalar} \"6\" -> {scalar} \"0\"", text);
        }
        else
        {
            Assert.Contains($"call 1 argument 0: {scalar} \"5\" -> {scalar} \"6\"", text);
            Assert.Contains($"call 1 argument 0: {scalar} \"5\" -> {scalar} \"7\"", text);
            Assert.Contains("call 1 receiver:", text);
            Assert.Contains($"::Value = {scalar} \"7\"", text);
            Assert.Contains($"::Value = {scalar} \"8\"", text);
            Assert.Contains($"::Value = {scalar} \"9\"", text);
        }

        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
    }

    private static async Task<string> ReadComparisonTranscriptAsync(IPage page, int maximumSnapshots = 24)
    {
        var screen = (await page.Locator("#terminal").BoundingBoxAsync())!;
        await page.Mouse.MoveAsync(screen.X + (screen.Width / 2), screen.Y + (screen.Height / 3));
        var snapshots = new List<string>();
        for (var attempt = 0; attempt < maximumSnapshots; attempt++)
        {
            var text = await BufferTextAsync(page);
            snapshots.Add(string.Join(" ", text.Replace("│", "", StringComparison.Ordinal).Replace("▉", "", StringComparison.Ordinal)
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));
            if (text.Contains("Copy: ", StringComparison.Ordinal))
            {
                return string.Join('\n', snapshots);
            }

            await page.Mouse.WheelAsync(0, -100);
            await page.WaitForFunctionAsync("""
                previous => Array.from({ length: window.ilreplTerminal.rows }, (_, row) =>
                  window.ilreplTerminal.buffer.active.getLine(row)?.translateToString(true) ?? '').join('\n') !== previous
                """, text, new() { Timeout = 10_000 });
        }

        Assert.Fail("The comparison heading was not reachable by scrolling the transcript.");
        return "";
    }

}
