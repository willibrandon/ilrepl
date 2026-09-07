using IlRepl.Engine;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Tests for <c>.override</c> in both places: inside a method, naming the slot the method fills,
/// and at class level, naming the slot and the method that fills it.
/// </summary>
[TestClass]
public sealed class OverrideParserTests
{
    private static readonly ParseContext Context = new([], [], GenericContext.Empty, new TypeResolver(), []);
    private static readonly TypeHeader Class = TypeHeaderParser.Parse("public Point {", nested: false);

    private static MethodSignature Member(string spec) => MethodHeaderParser.ParseMember(spec, Context, Class, out _, out _, out _);

    private static Session Load(params string[] lines)
    {
        var session = new Session();
        foreach (var line in lines)
        {
            session.AddLine(line);
        }

        return session;
    }

    /// <summary>
    /// The short form picks the overload from the method's own signature; the long form spells it out.
    /// </summary>
    [TestMethod]
    public void ParseInBody_ShortAndLongForms()
    {
        var method = Member("public virtual instance string Describe() {");
        var shortForm = OverrideParser.ParseInBody("[System.Runtime]System.Object::ToString", Context, method, ".override [System.Runtime]System.Object::ToString");
        Assert.AreEqual(typeof(object).GetMethod("ToString"), shortForm.Target);
        var longForm = OverrideParser.ParseInBody("method instance string [System.Runtime]System.Object::ToString()", Context, method, "");
        Assert.AreEqual(shortForm.Target, longForm.Target);
        var compare = Member("public virtual instance int32 CompareTo(object o) {");
        var target = OverrideParser.ParseInBody("[System.Runtime]System.IComparable::CompareTo", Context, compare, "").Target;
        Assert.AreEqual(typeof(IComparable).GetMethod("CompareTo"), target);
    }

    /// <summary>
    /// The target must be virtual, the method must be virtual, and the shapes must agree.
    /// </summary>
    [TestMethod]
    public void ParseInBody_ChecksTargetAndMethod()
    {
        var describe = Member("public virtual instance string Describe() {");
        Assert.Contains("is not virtual", Assert.ThrowsExactly<ReplException>(() => OverrideParser.ParseInBody("method instance class [System.Runtime]System.Type [System.Runtime]System.Object::GetType()", Context, describe, "")).Message);
        var plain = Member("public instance string Describe() {");
        Assert.Contains("must be virtual", Assert.ThrowsExactly<ReplException>(() => OverrideParser.ParseInBody("[System.Runtime]System.Object::ToString", Context, plain, "")).Message);
        var wrongShape = Member("public virtual instance int32 Describe() {");
        Assert.Contains("does not match", Assert.ThrowsExactly<ReplException>(() => OverrideParser.ParseInBody("method instance string [System.Runtime]System.Object::ToString()", Context, wrongShape, "")).Message);
        Assert.Contains("belongs at class level", Assert.ThrowsExactly<ReplException>(() => OverrideParser.ParseInBody("[System.Runtime]System.Object::ToString with method instance string Point::Describe()", Context, describe, "")).Message);
        Assert.Contains("usage: .override T::M", Assert.ThrowsExactly<ReplException>(() => OverrideParser.ParseInBody("ToString", Context, describe, "")).Message);
    }

    /// <summary>
    /// The class-level form records the slot and the name and shape of the implementing method.
    /// </summary>
    [TestMethod]
    public void ParseAtClassLevel_RecordsTargetAndBody()
    {
        var context = new ParseContext([], [], GenericContext.Empty, new TypeResolver(), []);
        var declaration = OverrideParser.ParseAtClassLevel("method instance string [System.Runtime]System.Object::ToString() with method instance string Point::Describe()", context, "");
        Assert.AreEqual(typeof(object).GetMethod("ToString"), declaration.Target);
        Assert.AreEqual("Describe", declaration.BodyName);
        Assert.AreEqual(typeof(string), declaration.BodyReturnType);
        Assert.IsEmpty(declaration.BodyParameterTypes);
        Assert.IsFalse(declaration.BodyIsStatic);
        Assert.Contains("belongs inside the method", Assert.ThrowsExactly<ReplException>(() => OverrideParser.ParseAtClassLevel("[System.Runtime]System.Object::ToString", context, "")).Message);
        Assert.Contains("names a method of this type", Assert.ThrowsExactly<ReplException>(() => OverrideParser.ParseAtClassLevel("[System.Runtime]System.Object::ToString with Describe", context, "")).Message);
    }

    /// <summary>
    /// Inside a session, an override in a body is kept on the method and one at class level on the type.
    /// </summary>
    [TestMethod]
    public void Session_KeepsOverridesWhereTheyWereWritten()
    {
        var session = Load(
            ".class public Named {",
            ".method public virtual instance string Describe() {",
            ".override [System.Runtime]System.Object::ToString",
            "ldstr \"named\"",
            "ret",
            "}",
            ".override method instance int32 [System.Runtime]System.Object::GetHashCode() with method instance int32 Named::Hash()",
            ".method public virtual instance int32 Hash() {",
            "ldc.i4 7",
            "ret",
            "}",
            "}");
        var type = session.Types[0].Declaration;
        Assert.AreEqual(typeof(object).GetMethod("ToString"), type.Methods[0].Overrides[0].Target);
        Assert.AreEqual(typeof(object).GetMethod("GetHashCode"), type.Overrides[0].Target);
        Assert.AreEqual("Hash", type.Overrides[0].BodyName);
        var unknown = Load(".class public Named {", ".override method instance int32 [System.Runtime]System.Object::GetHashCode() with method instance int32 Named::Hash()");
        Assert.Contains("Hash(), which Named does not declare", Assert.ThrowsExactly<ReplException>(() => unknown.AddLine("}")).Message);
    }
}
