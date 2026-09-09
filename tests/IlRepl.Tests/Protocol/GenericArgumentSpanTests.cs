using IlRepl.Protocol;

namespace IlRepl.Tests.Protocol;

/// <summary>
/// Generic argument discovery respects nested lists, quoted delimiters and function-pointer parameter lists.
/// </summary>
[TestClass]
public sealed class GenericArgumentSpanTests
{
    /// <summary>
    /// The innermost list is selected while its argument contains nested or quoted punctuation.
    /// </summary>
    [TestMethod]
    [DataRow("newobj Dictionary<string, List<int|>>", "int")]
    [DataRow("newobj Dictionary<string, List<int>, str|>", "string", "List<int>", "str")]
    [DataRow("call Host::M<method int32 *(int32, string), str|>", "method int32 *(int32, string)", "str")]
    [DataRow("newobj List<'Comma,Angle<Name'|>", "'Comma,Angle<Name'")]
    [DataRow("newobj List<str|", "str")]
    public void At_SplitsOnlyTheSelectedListsArguments(string marked, params string[] expected)
    {
        var caret = marked.IndexOf('|', StringComparison.Ordinal);
        var line = marked.Remove(caret, 1);
        var span = GenericArgumentSpan.At(line, caret, false);
        Assert.IsNotNull(span);
        Assert.AreSequenceEqual(expected, span.Arguments.ToArray());
    }

    /// <summary>
    /// A signature site recovers its just-closed method argument list rather than a nested argument's list.
    /// </summary>
    [TestMethod]
    public void AfterClose_SelectsTheMethodList()
    {
        const string line = "call Host::M<List<string>, int32> ";
        var span = GenericArgumentSpan.At(line, line.Length, false, afterClose: true);
        Assert.IsNotNull(span);
        string[] expected = ["List<string>", "int32"];
        Assert.AreSequenceEqual(expected, span.Arguments.ToArray());
    }
}
