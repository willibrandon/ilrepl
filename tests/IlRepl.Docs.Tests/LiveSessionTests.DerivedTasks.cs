using IlRepl.Tests.Shared;

namespace IlRepl.Docs.Tests;

/// <summary>
/// Browser comparison workers preserve derived tasks and observe their eventual completion.
/// </summary>
public sealed partial class LiveSessionTests
{
    /// <summary>
    /// A task subclass keeps its identity while completion records its result and deferred input mutation.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="kind">The non-generic, generic, or generically derived task shape.</param>
    /// <param name="fail">Whether task completion throws after mutating the input.</param>
    [TestMethod]
    [DataRow("chromium", 0, false)]
    [DataRow("webkit", 0, false)]
    [DataRow("chromium", 1, false)]
    [DataRow("webkit", 1, false)]
    [DataRow("chromium", 2, false)]
    [DataRow("webkit", 2, false)]
    [DataRow("chromium", 2, true)]
    [DataRow("webkit", 2, true)]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_DerivedTaskComparisonObservesCompletion(string browser, int kind, bool fail)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        page.Console += (_, message) => TestContext.WriteLine(message.Text);
        var parent = await ObserveComparisonResultsAsync(page);
        var task = kind == 2 ? "class Work`1<int32>" : "class Work";
        var source = DerivedTaskComparisonExamples.Source(kind, fail) + "\n.edit " + task + " Owner::Read(int32[]) as Copy {\n"
            + DerivedTaskComparisonExamples.Method(kind, 43) + "\n}\n" + DerivedTaskComparisonExamples.Scenario();
        await SubmitEditSourceAsync(page, source, "end of method Scenario");

        await TypeLineAsync(page, ".compare Copy using Scenario");
        var original = await WaitForComparisonResultAsync(parent, 0);
        var edited = await WaitForComparisonResultAsync(parent, 1);
        foreach (var (side, expected) in new[] { (original, "42"), (edited, "43") })
        {
            Assert.AreEqual("completed", side.GetProperty("outcome").GetString(), side.GetRawText());
            var invocations = side.GetProperty("invocations");
            Assert.AreEqual(1, invocations.GetArrayLength());
            var invocation = invocations[0];
            var input = invocation.GetProperty("inputs").EnumerateArray()
                .Single(member => member.GetProperty("name").GetString() == "argument 0").GetProperty("value");
            var output = invocation.GetProperty("outputs").EnumerateArray()
                .Single(member => member.GetProperty("name").GetString() == "argument 0").GetProperty("value");
            Assert.AreEqual("0", input.GetProperty("members")[0].GetProperty("value").GetProperty("value").GetString());
            Assert.AreEqual(expected, output.GetProperty("members")[0].GetProperty("value").GetProperty("value").GetString());
            if (fail)
            {
                var exception = side.GetProperty("exception");
                Assert.EndsWith("InvalidOperationException", exception.GetProperty("type").GetString()!);
                Assert.AreEqual("derived failure", exception.GetProperty("message").GetString());
                var observed = invocation.GetProperty("exception");
                Assert.AreEqual(exception.GetProperty("type").GetString(), observed.GetProperty("type").GetString());
                Assert.AreEqual(exception.GetProperty("message").GetString(), observed.GetProperty("message").GetString());
                Assert.AreEqual(exception.GetProperty("hResult").GetInt32(), observed.GetProperty("hResult").GetInt32());
            }
            else
            {
                Assert.IsFalse(side.TryGetProperty("exception", out _));
                Assert.IsFalse(invocation.TryGetProperty("exception", out _));
                var result = invocation.GetProperty("outputs").EnumerateArray()
                    .Single(member => member.GetProperty("name").GetString() == "return").GetProperty("value");
                Assert.AreEqual(kind == 0 ? "null" : "scalar", result.GetProperty("kind").GetString());
                if (kind != 0)
                {
                    Assert.AreEqual(expected, result.GetProperty("value").GetString());
                    Assert.AreEqual(expected, side.GetProperty("result").GetProperty("value").GetString());
                }
            }
        }

        await ExpectComparisonTextAsync(page, "Copy: different");
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
    }
}
