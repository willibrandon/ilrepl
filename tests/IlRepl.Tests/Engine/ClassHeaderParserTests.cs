using System.Reflection;
using IlRepl.Engine;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Tests for parsing <c>.class</c> headers: the ILAsm words, the name and its arity, generic
/// parameters with their constraints, and the base and interface texts kept for resolution.
/// </summary>
[TestClass]
public sealed class ClassHeaderParserTests
{
    /// <summary>
    /// A header yields the name, the attributes, the base text, and whether the brace was on the line.
    /// </summary>
    [TestMethod]
    public void Parse_Header_ReturnsNameAttributesAndBrace()
    {
        var header = TypeHeaderParser.Parse(" public sequential ansi sealed Point extends [System.Runtime]System.ValueType {", nested: false);
        Assert.AreEqual("Point", header.Name);
        Assert.AreEqual("", header.Namespace);
        Assert.AreEqual(TypeLayoutKind.Sequential, header.Layout);
        Assert.IsTrue(header.Attributes.HasFlag(TypeAttributes.Public));
        Assert.IsTrue(header.Attributes.HasFlag(TypeAttributes.Sealed));
        Assert.AreEqual("[System.Runtime]System.ValueType", header.BaseTypeText);
        Assert.IsTrue(header.OpensBlock);
        Assert.IsFalse(header.ClosesBlock);
    }

    /// <summary>
    /// A dotted name splits into a namespace and a name, and a quoted name loses its quotes.
    /// </summary>
    [TestMethod]
    public void Parse_DottedAndQuotedNames()
    {
        var dotted = TypeHeaderParser.Parse("public Geometry.Shapes.Point", nested: false);
        Assert.AreEqual("Geometry.Shapes", dotted.Namespace);
        Assert.AreEqual("Point", dotted.Name);
        var quoted = TypeHeaderParser.Parse("public 'My Type' {", nested: false);
        Assert.AreEqual("My Type", quoted.Name);
    }

    /// <summary>
    /// <c>interface</c>, <c>value</c>, and <c>enum</c> decide the kind on their own.
    /// </summary>
    [TestMethod]
    public void Parse_KindWords_AreRecorded()
    {
        Assert.AreEqual(TypeKind.Interface, TypeHeaderParser.Parse("interface public abstract IShape {", nested: false).Kind);
        Assert.AreEqual(TypeKind.Struct, TypeHeaderParser.Parse("public value Pair", nested: false).Kind);
        Assert.AreEqual(TypeKind.Enum, TypeHeaderParser.Parse("public enum Color", nested: false).Kind);
        Assert.IsTrue(TypeHeaderParser.Parse("interface public IShape", nested: false).KindFromWord);
        Assert.IsFalse(TypeHeaderParser.Parse("public Point extends System.ValueType", nested: false).KindFromWord);
    }

    /// <summary>
    /// An interface with <c>extends</c> is refused the way ILAsm refuses it.
    /// </summary>
    [TestMethod]
    public void Parse_InterfaceWithExtends_Throws()
    {
        var ex = Assert.ThrowsExactly<ReplException>(() => TypeHeaderParser.Parse("interface IShape extends Object", nested: false));
        Assert.Contains("cannot extend a class", ex.Message);
    }

    /// <summary>
    /// <c>implements</c> lists every interface text, and <c>extends</c> stops where it begins.
    /// </summary>
    [TestMethod]
    public void Parse_Implements_ListsInterfaces()
    {
        var header = TypeHeaderParser.Parse("public Square extends Shape implements IArea, [System.Runtime]System.IDisposable {", nested: false);
        Assert.AreEqual("Shape", header.BaseTypeText);
        Assert.AreSequenceEqual(["IArea", "[System.Runtime]System.IDisposable"], header.InterfaceTexts);
    }

    /// <summary>
    /// Nested visibility words parse inside a class, and the plain words are rewritten to nested ones there.
    /// </summary>
    [TestMethod]
    public void Parse_NestedVisibility()
    {
        Assert.AreEqual(TypeAttributes.NestedFamily, TypeHeaderParser.Parse("nested family Inner", nested: true).Attributes & TypeAttributes.VisibilityMask);
        Assert.AreEqual(TypeAttributes.NestedPublic, TypeHeaderParser.Parse("public Inner", nested: true).Attributes & TypeAttributes.VisibilityMask);
        Assert.AreEqual(TypeAttributes.NestedPrivate, TypeHeaderParser.Parse("Inner", nested: true).Attributes & TypeAttributes.VisibilityMask);
        Assert.AreEqual(TypeAttributes.Public, TypeHeaderParser.Parse("nested public Top", nested: false).Attributes & TypeAttributes.VisibilityMask);
        Assert.AreEqual(TypeAttributes.NotPublic, TypeHeaderParser.Parse("Top", nested: false).Attributes & TypeAttributes.VisibilityMask);
    }

    /// <summary>
    /// Generic parameters record their names, variance, and constraint texts, and the arity is appended.
    /// </summary>
    [TestMethod]
    public void Parse_GenericParameters_WithConstraintsAndVariance()
    {
        var header = TypeHeaderParser.Parse("interface public ISource<+T> {", nested: false);
        Assert.AreEqual("ISource`1", header.Name);
        Assert.IsTrue(header.GenericParameters[0].Attributes.HasFlag(GenericParameterAttributes.Covariant));

        var pair = TypeHeaderParser.Parse("public Pair<class .ctor ([System.Runtime]System.IComparable) T, valuetype U> {", nested: false);
        Assert.AreEqual("Pair`2", pair.Name);
        Assert.AreEqual("T", pair.GenericParameters[0].Name);
        Assert.IsTrue(pair.GenericParameters[0].Attributes.HasFlag(GenericParameterAttributes.ReferenceTypeConstraint));
        Assert.IsTrue(pair.GenericParameters[0].Attributes.HasFlag(GenericParameterAttributes.DefaultConstructorConstraint));
        Assert.AreSequenceEqual(["[System.Runtime]System.IComparable"], pair.GenericParameters[0].ConstraintTexts);
        Assert.IsTrue(pair.GenericParameters[1].Attributes.HasFlag(GenericParameterAttributes.NotNullableValueTypeConstraint));
        Assert.AreEqual("Set`1", TypeHeaderParser.Parse("public Set`1<T>", nested: false).Name);
    }

    /// <summary>
    /// Variance is refused on a class, and a parameter cannot be both class and valuetype.
    /// </summary>
    [TestMethod]
    public void Parse_GenericParameterRules()
    {
        Assert.Contains("only allowed on interface", Assert.ThrowsExactly<ReplException>(() => TypeHeaderParser.Parse("public Box<+T>", nested: false)).Message);
        Assert.Contains("both class and valuetype", Assert.ThrowsExactly<ReplException>(() => TypeHeaderParser.Parse("public Box<class valuetype T>", nested: false)).Message);
        Assert.Contains("declared twice", Assert.ThrowsExactly<ReplException>(() => TypeHeaderParser.Parse("public Box<T, T>", nested: false)).Message);
    }

    /// <summary>
    /// <c>{ }</c> on the header opens and closes an empty type; unsupported words and the reserved namespace are refused.
    /// </summary>
    [TestMethod]
    public void Parse_EmptyBlockAndRefusals()
    {
        var empty = TypeHeaderParser.Parse("public Empty { }", nested: false);
        Assert.IsTrue(empty.OpensBlock);
        Assert.IsTrue(empty.ClosesBlock);
        Assert.Contains("not supported", Assert.ThrowsExactly<ReplException>(() => TypeHeaderParser.Parse("public import Foo", nested: false)).Message);
        Assert.Contains("reserved", Assert.ThrowsExactly<ReplException>(() => TypeHeaderParser.Parse("public IlRepl.Cell", nested: false)).Message);
        Assert.Contains("usage", Assert.ThrowsExactly<ReplException>(() => TypeHeaderParser.Parse("public {", nested: false)).Message);
    }
}
