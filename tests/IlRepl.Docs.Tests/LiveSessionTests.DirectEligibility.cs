using IlRepl.Tests.Shared;

namespace IlRepl.Docs.Tests;

/// <summary>
/// Browser direct comparisons reject incompatible receiver and generic requirements before launching either worker.
/// </summary>
public sealed partial class LiveSessionTests
{
    /// <summary>
    /// Receiver changes reject atomically, instance direct calls launch no workers, and retained copies compare successfully.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="originalStatic">Whether the captured original is static.</param>
    /// <param name="editedStatic">Whether the proposed edited method is static.</param>
    /// <param name="generic">Whether the selected method is closed over int32.</param>
    /// <returns>The completed preflight and real-worker recovery assertions.</returns>
    [TestMethod]
    [DataRow("chromium", false, true, false)]
    [DataRow("webkit", false, true, false)]
    [DataRow("chromium", true, false, false)]
    [DataRow("webkit", true, false, false)]
    [DataRow("chromium", false, false, false)]
    [DataRow("webkit", false, false, false)]
    [DataRow("chromium", false, true, true)]
    [DataRow("webkit", false, true, true)]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_DirectComparisonRejectsEitherReceiver(string browser, bool originalStatic,
        bool editedStatic, bool generic)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        var parent = await ObserveComparisonResultsAsync(page);
        var workers = 0;
        page.Worker += (_, worker) =>
        {
            if (worker.Url.EndsWith("/comparison-worker.js", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref workers);
            }
        };
        await SubmitEditSourceAsync(page, DirectComparisonEligibilityExamples.Source(originalStatic, generic)
            + "\n.edit " + DirectComparisonEligibilityExamples.Reference(originalStatic, generic) + " as Copy {\n"
            + DirectComparisonEligibilityExamples.Method(originalStatic, generic, true) + "\n}", "edit Copy committed as revision 1");
        if (originalStatic != editedStatic)
        {
            await SubmitEditSourceAsync(page, ".edit Copy {\n"
                + DirectComparisonEligibilityExamples.Method(editedStatic, generic, true)
                    .Replace("ldc.i4.s 42", "ldc.i4.s 99", StringComparison.Ordinal) + "\n}",
                "incomplete on line");
            await PromptContainsAsync(page, "}");
            await ClearPromptAsync(page);
        }

        if (!originalStatic)
        {
            await TypeLineAsync(page, ".compare Copy ()");
            await ExpectCompletionAsync(page, "the original Copy is an instance method");
            Assert.Contains("matching original and edited signatures", DirectEligibilityText(await BufferTextAsync(page)));
        }

        await InputIdleAsync(page);
        await EmptyPromptAsync(page);

        Assert.AreEqual(0, Volatile.Read(ref workers));
        Assert.AreEqual(0, await parent.EvaluateAsync<int>("() => self.comparisonResults.length"));
        var text = DirectEligibilityText(await BufferTextAsync(page));
        Assert.DoesNotContain("setup-failed", text);
        Assert.DoesNotContain("IndexOutOfRangeException", text);
        await RunCorpusCellAsync(page, "ldsfld int32 Owner::Runs\nret", 0);
        await RunCorpusCellAsync(page, "ldsfld int32 IlRepl.Edits.Copy.Owner::Runs\nret", 0);
        if (!originalStatic)
        {
            await SubmitEditSourceAsync(page, DirectComparisonEligibilityExamples.Scenario(false), "end of method Scenario");
        }

        await TypeLineAsync(page, originalStatic ? ".compare Copy ()" : ".compare Copy using Scenario");
        await ExpectComparisonTextAsync(page, "Copy: different");
        var before = await WaitForComparisonResultAsync(parent, 0);
        var after = await WaitForComparisonResultAsync(parent, 1);
        Assert.AreEqual(2, Volatile.Read(ref workers));
        Assert.AreEqual("completed", before.GetProperty("outcome").GetString());
        Assert.AreEqual("completed", after.GetProperty("outcome").GetString());
        Assert.AreEqual("41", before.GetProperty("result").GetProperty("value").GetString());
        Assert.AreEqual("42", after.GetProperty("result").GetProperty("value").GetString());
        Assert.AreEqual(1, before.GetProperty("invocations").GetArrayLength());
        Assert.AreEqual(1, after.GetProperty("invocations").GetArrayLength());
        await RunCorpusCellAsync(page, originalStatic ? "call Copy\nret" : "call Scenario\nret", 42);
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
    }

    /// <summary>
    /// Open generic arguments fail before worker creation and leave both declarations unexecuted.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <returns>The completed generic preflight assertions.</returns>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_DirectComparisonRejectsOpenGeneric(string browser)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        var workers = 0;
        page.Worker += (_, worker) =>
        {
            if (worker.Url.EndsWith("/comparison-worker.js", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref workers);
            }
        };
        await SubmitEditSourceAsync(page, DirectComparisonEligibilityExamples.Source(true, true)
            + "\n.edit " + DirectComparisonEligibilityExamples.Reference(true, true, false) + " as Copy {\n"
            + DirectComparisonEligibilityExamples.Method(true, true, true) + "\n}", "edit Copy committed as revision 1");

        await TypeLineAsync(page, ".compare Copy ()");
        await ExpectCompletionAsync(page, "the original Copy has unbound generic parameters");
        await InputIdleAsync(page);
        await EmptyPromptAsync(page);

        Assert.AreEqual(0, Volatile.Read(ref workers));
        var text = DirectEligibilityText(await BufferTextAsync(page));
        Assert.Contains("both versions to be closed", text);
        Assert.Contains("Select a closed generic method with .edit", text);
        Assert.DoesNotContain("setup-failed", text);
        await RunCorpusCellAsync(page, "ldsfld int32 Owner::Runs\nret", 0);
        await RunCorpusCellAsync(page, "ldsfld int32 IlRepl.Edits.Copy.Owner::Runs\nret", 0);
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
    }

    private static string DirectEligibilityText(string text) => string.Join(" ", text.Replace('│', ' ').Replace('▉', ' ')
        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
