namespace IlRepl.Docs.Tests;

/// <summary>
/// Browser edits preserve parameter names and flags in reflection, comparison observations, and saved assemblies.
/// </summary>
public sealed partial class LiveSessionTests
{
    /// <summary>
    /// Renamed parameters and changed flags remain visible after an edit and determine which input values are observed.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="before">The original parameter flags.</param>
    /// <param name="after">The edited parameter flags.</param>
    /// <param name="attributes">The expected edited parameter attribute bits.</param>
    [TestMethod]
    [DataRow("chromium", "", "[out]", 2)]
    [DataRow("webkit", "", "[out]", 2)]
    [DataRow("chromium", "[out]", "", 0)]
    [DataRow("webkit", "[out]", "", 0)]
    [DataRow("chromium", "[out]", "[in] [out] [opt]", 19)]
    [DataRow("webkit", "[out]", "[in] [out] [opt]", 19)]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_EditPreservesParameterMetadata(string browser, string before, string after, int attributes)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        var parent = await ObserveComparisonResultsAsync(page);
        await SubmitEditSourceAsync(page, $$"""
            .class public Owner {
              .method private static void Set({{before}} int32& original) {
                ldarg original
                ldc.i4.s 42
                stind.i4
                ret
              }
            }
            .edit void Owner::Set(int32&) as Copy {
              .method private static void Set({{after}} int32& renamed) {
                ldarg renamed
                ldc.i4.s 43
                stind.i4
                ret
              }
            }
            .method int32 Scenario() {
              .locals init (int32 value)
              ldc.i4 123
              stloc.0
              ldloca.s 0
              call Copy
              ldloc.0
              ret
            }
            .method class System.Reflection.ParameterInfo Parameter() {
              ldtoken method void Copy(int32&)
              call System.Reflection.MethodBase::GetMethodFromHandle(valuetype RuntimeMethodHandle)
              callvirt System.Reflection.MethodBase::GetParameters()
              ldc.i4.0
              ldelem.ref
              ret
            }
            """, "end of method Parameter");
        await RunCorpusCellAsync(page, "call Parameter\ncallvirt System.Reflection.ParameterInfo::get_Attributes()\nconv.i4\nret",
            attributes);
        await RunCorpusCellAsync(page, "call Parameter\ncallvirt System.Reflection.ParameterInfo::get_Name()\nldstr \"renamed\"\n"
            + "call bool String::op_Equality(string, string)\nconv.i4\nret", 1);
        await TypeLineAsync(page, ".compare Copy using Scenario");
        await ExpectComparisonTextAsync(page, "Copy: different-inputs");
        var original = await WaitForComparisonResultAsync(parent, 0);
        var edited = await WaitForComparisonResultAsync(parent, 1);
        Assert.AreEqual("42", original.GetProperty("result").GetProperty("value").GetString());
        Assert.AreEqual("43", edited.GetProperty("result").GetProperty("value").GetString());
        foreach (var (side, flags) in new[] { (original, before), (edited, after) })
        {
            var input = side.GetProperty("invocations")[0].GetProperty("inputs").EnumerateArray()
                .Single(member => member.GetProperty("name").GetString() == "argument 0").GetProperty("value");
            var onlyOut = flags.Contains("[out]", StringComparison.Ordinal) && !flags.Contains("[in]", StringComparison.Ordinal);
            Assert.AreEqual(onlyOut ? "null" : "scalar", input.GetProperty("kind").GetString());
            Assert.AreEqual(!onlyOut, input.TryGetProperty("value", out var value));
            if (!onlyOut)
            {
                Assert.AreEqual("123", value.GetString());
            }
        }

        await TypeLineAsync(page, "call Scenario");
        await TypeLineAsync(page, ".save /tmp/edited-parameters.dll");
        await ExpectCompletionAsync(page, "wrote /tmp/edited-parameters.dll");
        await TypeLineAsync(page, ".reset");
        await ExpectCompletionAsync(page, "cell, declarations, methods, and types cleared");
        await TypeLineAsync(page, ".load /tmp/edited-parameters.dll");
        await ExpectCompletionAsync(page, "public types)");
        await RunCorpusCellAsync(page, "call object [edited-parameters]IlRepl.Cell::Run()\nret", 43);
        await RunCorpusCellAsync(page, "call [edited-parameters]IlRepl.Cell::Parameter()\n"
            + "callvirt System.Reflection.ParameterInfo::get_Attributes()\nconv.i4\nret", attributes);
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
    }
}
