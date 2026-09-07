using IlRepl.Engine;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Tests for parsing <c>.custom</c> lines in both the blob form ildasm writes and the typed form.
/// </summary>
[TestClass]
public sealed class CustomAttributeParserTests
{
    private static readonly ParseContext Context = new([], [], GenericContext.Empty, new TypeResolver(), []);

    private static CustomAttributeDeclaration Parse(string spec) => CustomAttributeParser.Parse(spec, Context, ".custom " + spec);

    /// <summary>
    /// A blob decodes against the constructor: prolog, a string, and no named arguments.
    /// </summary>
    [TestMethod]
    public void Parse_Blob_DecodesFixedArguments()
    {
        var attribute = Parse("instance void [System.Runtime]System.ObsoleteAttribute::.ctor(string) = ( 01 00 03 6F 6C 64 00 00 )");
        Assert.AreEqual(typeof(ObsoleteAttribute), attribute.AttributeType);
        Assert.AreEqual("old", attribute.FixedArguments[0]);
        Assert.IsEmpty(attribute.NamedFields);
        Assert.AreEqual("[ObsoleteAttribute(\"old\")]", attribute.Describe());
    }

    /// <summary>
    /// A blob with a named property decodes it; leftover bytes and a bad prolog are refused.
    /// </summary>
    [TestMethod]
    public void Parse_Blob_NamedPropertyAndErrors()
    {
        var attribute = Parse("instance void [System.Runtime]System.ObsoleteAttribute::.ctor() = ( 01 00 01 00 54 02 07 49 73 45 72 72 6F 72 01 )");
        Assert.AreEqual("IsError", attribute.NamedProperties[0].Property.Name);
        Assert.IsTrue((bool)attribute.NamedProperties[0].Value!);
        Assert.Contains("starts with the prolog", Assert.ThrowsExactly<ReplException>(() => Parse("instance void [System.Runtime]System.ObsoleteAttribute::.ctor() = ( 02 00 00 00 )")).Message);
        Assert.Contains("bytes left over", Assert.ThrowsExactly<ReplException>(() => Parse("instance void [System.Runtime]System.ObsoleteAttribute::.ctor() = ( 01 00 00 00 FF )")).Message);
    }

    /// <summary>
    /// The typed form takes constructor arguments by their types and named members by name.
    /// </summary>
    [TestMethod]
    public void Parse_Typed_ArgumentsAndNamed()
    {
        var attribute = Parse("instance void [System.Runtime]System.ObsoleteAttribute::.ctor(string, bool) = { string('gone') bool(true) property bool IsError = bool(false) }");
        Assert.AreEqual("gone", attribute.FixedArguments[0]);
        Assert.IsTrue((bool)attribute.FixedArguments[1]!);
        Assert.IsFalse((bool)attribute.NamedProperties[0].Value!);
        var none = Parse("instance void [System.Runtime]System.FlagsAttribute::.ctor()");
        Assert.IsEmpty(none.FixedArguments);
        Assert.Contains("takes 1 argument(s)", Assert.ThrowsExactly<ReplException>(() => Parse("instance void [System.Runtime]System.ObsoleteAttribute::.ctor(string)")).Message);
    }

    /// <summary>
    /// A type argument is written as type(...), and an attribute constructor must belong to an attribute.
    /// </summary>
    [TestMethod]
    public void Parse_TypeArgumentAndRefusals()
    {
        var attribute = Parse("instance void [System.Runtime]System.Diagnostics.DebuggerTypeProxyAttribute::.ctor(class [System.Runtime]System.Type) = { type(int32) }");
        Assert.AreEqual(typeof(int), attribute.FixedArguments[0]);
        Assert.Contains("is not an attribute type", Assert.ThrowsExactly<ReplException>(() => Parse("instance void [System.Runtime]System.Object::.ctor()")).Message);
        Assert.Contains("write type(Name)", Assert.ThrowsExactly<ReplException>(() => Parse("instance void [System.Runtime]System.Diagnostics.DebuggerTypeProxyAttribute::.ctor(class [System.Runtime]System.Type) = { string('x') }")).Message);
    }
}
