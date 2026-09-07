using IlRepl.Engine;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Tests for <see cref="TypeRelations"/>: the base chain and interfaces of session types come
/// from their declarations, and assignability follows them.
/// </summary>
[TestClass]
public sealed class TypeRelationsTests
{
    private static Session Load(params string[] lines)
    {
        var session = new Session();
        foreach (var line in lines)
        {
            session.AddLine(line);
        }

        return session;
    }

    private static Type Find(Session session, string name)
    {
        Assert.IsTrue(session.TypeTable.TryResolve(name, false, false, out var type), name);
        return type;
    }

    /// <summary>
    /// A session class knows its base and interfaces before it is created.
    /// </summary>
    [TestMethod]
    public void BaseAndInterfaces_ComeFromDeclarations()
    {
        var session = Load(
            ".class interface public abstract IShape { }",
            ".class public abstract Shape implements IShape { }",
            ".class public Circle extends Shape implements [System.Runtime]System.IDisposable {",
            ".method public virtual instance void Dispose() {",
            "ret",
            "}",
            "}");
        var table = session.TypeTable;
        var shape = Find(session, "Shape");
        var circle = Find(session, "Circle");
        var ishape = Find(session, "IShape");
        Assert.AreEqual(shape, TypeRelations.BaseTypeOf(circle, table));
        Assert.AreEqual(typeof(object), TypeRelations.BaseTypeOf(shape, table));
        Assert.IsNull(TypeRelations.BaseTypeOf(ishape, table));
        var all = TypeRelations.AllInterfacesOf(circle, table);
        Assert.HasCount(2, all);
        Assert.Contains(typeof(IDisposable), all);
        Assert.Contains(ishape, all);
        Assert.IsTrue(TypeRelations.IsSubclassOf(circle, shape, table));
        Assert.IsTrue(TypeRelations.IsSubclassOf(circle, typeof(object), table));
        Assert.IsFalse(TypeRelations.IsSubclassOf(shape, circle, table));
        Assert.IsTrue(TypeRelations.IsSessionType(circle));
        Assert.IsTrue(TypeRelations.IsSessionType(circle.MakeArrayType()));
        Assert.IsFalse(TypeRelations.IsSessionType(typeof(object)));
    }

    /// <summary>
    /// Assignability walks the session hierarchy, framework types keep reflection's answer.
    /// </summary>
    [TestMethod]
    public void IsAssignable_WalksSessionHierarchy()
    {
        var session = Load(
            ".class interface public abstract IShape { }",
            ".class public abstract Shape implements IShape { }",
            ".class public Circle extends Shape { }",
            ".class public Square extends Shape { }");
        var table = session.TypeTable;
        var shape = Find(session, "Shape");
        var circle = Find(session, "Circle");
        var square = Find(session, "Square");
        var ishape = Find(session, "IShape");
        Assert.IsTrue(TypeRelations.IsAssignable(circle, shape, table));
        Assert.IsTrue(TypeRelations.IsAssignable(circle, ishape, table));
        Assert.IsTrue(TypeRelations.IsAssignable(circle, typeof(object), table));
        Assert.IsFalse(TypeRelations.IsAssignable(shape, circle, table));
        Assert.IsFalse(TypeRelations.IsAssignable(circle, square, table));
        Assert.IsFalse(TypeRelations.IsAssignable(circle, typeof(IDisposable), table));
        Assert.IsTrue(TypeRelations.IsAssignable(circle.MakeArrayType(), shape.MakeArrayType(), table), "arrays of reference types are covariant");
        Assert.IsTrue(TypeRelations.IsAssignable(circle.MakeArrayType(), typeof(Array), table));
        Assert.IsTrue(TypeRelations.IsAssignable(typeof(string), typeof(object), table));
        Assert.IsFalse(TypeRelations.IsAssignable(typeof(object), typeof(string), table));
    }

    /// <summary>
    /// A session generic interface is matched by instantiation, with variance where declared.
    /// </summary>
    [TestMethod]
    public void IsAssignable_GenericInstantiationsAndVariance()
    {
        var session = Load(
            ".class interface public abstract IBox`1<T> { }",
            ".class interface public abstract IOut`1<+T> { }",
            ".class public Box`1<T> implements class IBox`1<!0>, class IOut`1<!0> { }");
        var table = session.TypeTable;
        var box = Find(session, "Box`1");
        var ibox = Find(session, "IBox`1");
        var iout = Find(session, "IOut`1");
        var boxOfString = box.MakeGenericType(typeof(string));
        Assert.IsTrue(TypeRelations.IsAssignable(boxOfString, ibox.MakeGenericType(typeof(string)), table));
        Assert.IsFalse(TypeRelations.IsAssignable(boxOfString, ibox.MakeGenericType(typeof(object)), table), "IBox is invariant");
        Assert.IsTrue(TypeRelations.IsAssignable(boxOfString, iout.MakeGenericType(typeof(object)), table), "IOut is covariant");
        Assert.IsFalse(TypeRelations.IsAssignable(box.MakeGenericType(typeof(int)), iout.MakeGenericType(typeof(object)), table), "variance never applies to value types");
        Assert.IsTrue(TypeIdentity.Equal(ibox.MakeGenericType(typeof(string)), TypeRelations.DeclaredInterfacesOf(boxOfString, table)[0]));
    }

    /// <summary>
    /// Substitution rewrites a definition's parameters inside every constructed form.
    /// </summary>
    [TestMethod]
    public void Substitute_RewritesEveryForm()
    {
        var t = typeof(List<>).GetGenericArguments()[0];
        Type[] from = [t];
        Type[] to = [typeof(int)];
        Assert.AreEqual(typeof(int), TypeRelations.Substitute(t, from, to));
        Assert.AreEqual(typeof(int[]), TypeRelations.Substitute(t.MakeArrayType(), from, to));
        Assert.AreEqual(typeof(int).MakeArrayType(1), TypeRelations.Substitute(t.MakeArrayType(1), from, to));
        Assert.AreEqual(typeof(int).MakeByRefType(), TypeRelations.Substitute(t.MakeByRefType(), from, to));
        Assert.AreEqual(typeof(IEnumerable<int>), TypeRelations.Substitute(typeof(IEnumerable<>).MakeGenericType(t), from, to));
        Assert.AreEqual(typeof(string), TypeRelations.Substitute(typeof(string), from, to));
        Assert.AreEqual(typeof(List<int>), TypeRelations.SubstituteFor(typeof(Dictionary<int, string>), typeof(List<>).MakeGenericType(typeof(Dictionary<,>).GetGenericArguments()[0])));
    }

    /// <summary>
    /// Nesting is judged by definitions.
    /// </summary>
    [TestMethod]
    public void Nesting_ByDefinition()
    {
        Assert.AreEqual(typeof(Dictionary<,>), TypeRelations.Outermost(typeof(Dictionary<int, string>.Enumerator)));
        Assert.IsTrue(TypeRelations.IsWithin(typeof(Dictionary<int, string>.Enumerator), typeof(Dictionary<,>)));
        Assert.IsTrue(TypeRelations.IsWithin(typeof(Dictionary<,>), typeof(Dictionary<,>)));
        Assert.IsFalse(TypeRelations.IsWithin(typeof(Dictionary<,>), typeof(Dictionary<,>.Enumerator)));
    }
}
