using IlRepl.Repl;

namespace IlRepl.Tests.Repl;

/// <summary>
/// Tests for <see cref="ValueFormatter"/>.
/// </summary>
[TestClass]
public sealed class ValueFormatterTests
{
    private static readonly int[] Numbers = [1, 2, 3];

    private static string Text(object? value) => string.Concat(ValueFormatter.FormatWithType(value).Select(s => s.Text));

    /// <summary>
    /// Scalars render with their type.
    /// </summary>
    [TestMethod]
    public void FormatWithType_Scalars()
    {
        Assert.AreEqual("42 : int32", Text(42));
        Assert.AreEqual("\"hi\" : string", Text("hi"));
        Assert.AreEqual("'c' : char", Text('c'));
        Assert.AreEqual("true : bool", Text(true));
        Assert.AreEqual("null", Text(null));
        Assert.AreEqual("2.5 : float64", Text(2.5));
    }

    /// <summary>
    /// Arrays and collections expand, and long ones are cut.
    /// </summary>
    [TestMethod]
    public void FormatWithType_Sequences()
    {
        Assert.AreEqual("[1, 2, 3] : int32[]", Text(Numbers));
        Assert.AreEqual("{\"a\", \"b\"} : List<string>", Text(new List<string> { "a", "b" }));
        var many = Text(Enumerable.Range(0, 100).ToArray());
        Assert.Contains("…", many);
    }

    /// <summary>
    /// Objects without a useful ToString show their type in braces.
    /// </summary>
    [TestMethod]
    public void FormatWithType_PlainObject_ShowsType()
    {
        Assert.AreEqual("{object} : object", Text(new object()));
    }
}
