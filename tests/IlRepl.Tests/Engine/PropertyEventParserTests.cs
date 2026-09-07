using System.Reflection;
using IlRepl.Engine;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Tests for <c>.property</c> and <c>.event</c> headers and the accessor lines inside them.
/// </summary>
[TestClass]
public sealed class PropertyEventParserTests
{
    private static readonly ParseContext Context = new([], [], GenericContext.Empty, new TypeResolver(), []);

    /// <summary>
    /// A property header carries its type, index parameters, and whether it is an instance property.
    /// </summary>
    [TestMethod]
    public void ParseProperty_HeaderShapes()
    {
        var header = PropertyEventParser.ParseProperty("instance int32 Length() {", Context);
        Assert.AreEqual("Length", header.Name);
        Assert.AreEqual(typeof(int), header.Type);
        Assert.IsFalse(header.IsStatic);
        Assert.IsTrue(header.OpensBlock);
        Assert.IsEmpty(header.ParameterTypes);
        var indexer = PropertyEventParser.ParseProperty("specialname instance string Item(int32, string)", Context);
        Assert.AreSequenceEqual([typeof(int), typeof(string)], indexer.ParameterTypes);
        Assert.AreEqual(PropertyAttributes.SpecialName, indexer.Attributes);
        Assert.IsFalse(indexer.OpensBlock);
        var statics = PropertyEventParser.ParseProperty("int32 Count() {", Context);
        Assert.IsTrue(statics.IsStatic);
        Assert.Contains("usage: .property", Assert.ThrowsExactly<ReplException>(() => PropertyEventParser.ParseProperty("int32 Count {", Context)).Message);
        Assert.Contains("bad property name", Assert.ThrowsExactly<ReplException>(() => PropertyEventParser.ParseProperty("int32 1st() {", Context)).Message);
    }

    /// <summary>
    /// An event needs a delegate type and a name.
    /// </summary>
    [TestMethod]
    public void ParseEvent_NeedsDelegateType()
    {
        var header = PropertyEventParser.ParseEvent("class [System.Runtime]System.EventHandler Changed {", Context);
        Assert.AreEqual("Changed", header.Name);
        Assert.AreEqual(typeof(EventHandler), header.HandlerType);
        Assert.IsTrue(header.OpensBlock);
        Assert.Contains("is not a delegate type", Assert.ThrowsExactly<ReplException>(() => PropertyEventParser.ParseEvent("int32 Changed {", Context)).Message);
        Assert.Contains("usage: .event", Assert.ThrowsExactly<ReplException>(() => PropertyEventParser.ParseEvent("class [System.Runtime]System.EventHandler {", Context)).Message);
    }

    /// <summary>
    /// An accessor line names the method by its full shape.
    /// </summary>
    [TestMethod]
    public void ParseAccessor_NamesTheMethod()
    {
        var getter = PropertyEventParser.ParseAccessor("get", "instance int32 Point::get_Length()", Context);
        Assert.AreEqual("get", getter.Kind);
        Assert.AreEqual("get_Length", getter.Name);
        Assert.AreEqual(typeof(int), getter.ReturnType);
        Assert.IsFalse(getter.IsStatic);
        var setter = PropertyEventParser.ParseAccessor("set", "void Point::set_Length(int32)", Context);
        Assert.IsTrue(setter.IsStatic);
        Assert.AreSequenceEqual([typeof(int)], setter.ParameterTypes);
        Assert.AreEqual("get_Length", PropertyEventParser.ParseAccessor("get", "instance int32 get_Length()", Context).Name, "the type qualifier is optional");
        Assert.Contains("usage: .get", Assert.ThrowsExactly<ReplException>(() => PropertyEventParser.ParseAccessor("get", "instance int32 get_Length", Context)).Message);
    }
}
