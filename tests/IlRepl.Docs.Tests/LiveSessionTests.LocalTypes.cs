namespace IlRepl.Docs.Tests;

/// <summary>
/// Browser edits preserve local signature dependencies in comparisons and independently loaded exports.
/// </summary>
public sealed partial class LiveSessionTests
{
    /// <summary>
    /// Local modifiers and function pointers retain their original session types after replacement and export.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="pointer">Whether the local stores a function pointer instead of a modified integer.</param>
    /// <returns>The completed dependency, comparison, and saved assembly assertions.</returns>
    [TestMethod]
    [DataRow("chromium", false)]
    [DataRow("webkit", false)]
    [DataRow("chromium", true)]
    [DataRow("webkit", true)]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_EditPreservesExactLocalTypes(string browser, bool pointer)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        var local = pointer ? "method void *(class Marker)" : "int32 modopt(Marker)";
        string Source(int value) => ".method public static int32 Read() cil managed {\n.locals init (" + local
            + " value)\nldc.i4.s " + value + "\nret\n}";
        await SubmitEditSourceAsync(page, ".class public Marker {\n.field public int32 Original\n}\n" + Source(41)
            + "\n.edit Read as Copy {\n" + Source(42) + "\n}", "edit Copy committed as revision 1");
        await SubmitEditSourceAsync(page, ".class public Marker {\n.field public int64 Added\n}\n.edit Copy {\n"
            + Source(43) + "\n}", "edit Copy committed as revision 2");
        await TypeLineAsync(page, ".methods Copy");
        await ExpectCompletionAsync(page, "copied (distinct type identity)");
        await TypeLineAsync(page, ".compare Copy ()");
        await ExpectComparisonTextAsync(page, "Copy: different");
        var text = await BufferTextAsync(page);
        Assert.Contains("original: completed", text);
        Assert.Contains("edited: completed", text);
        Assert.Contains("\"41\"", text);
        Assert.Contains("\"43\"", text);
        await SubmitEditSourceAsync(page, ".method int32 Scenario() {\ncall Copy\nret\n}", "end of method Scenario");
        await TypeLineAsync(page, ".save /tmp/local-types.dll");
        await ExpectCompletionAsync(page, "wrote /tmp/local-types.dll");
        await TypeLineAsync(page, ".reset");
        await ExpectCompletionAsync(page, "cell, declarations, methods, and types cleared");
        await TypeLineAsync(page, ".load /tmp/local-types.dll");
        await ExpectCompletionAsync(page, "public types)");
        await RunCorpusCellAsync(page, "call int32 [local-types]IlRepl.Cell::Scenario()\nret", 43);
        Assert.DoesNotContain("error:", await BufferTextAsync(page));
    }
}
