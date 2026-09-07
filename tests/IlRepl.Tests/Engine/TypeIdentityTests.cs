using IlRepl.Engine;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Tests for <see cref="TypeIdentity"/>: generic parameters are the same only by owner, kind,
/// and position; constructed types compare structurally.
/// </summary>
[TestClass]
public sealed class TypeIdentityTests
{
    /// <summary>
    /// A type's parameter is never a method's, and one type's parameter is never another's.
    /// </summary>
    [TestMethod]
    public void Equal_GenericParameters_ByOwnerKindAndPosition()
    {
        var listT = typeof(List<>).GetGenericArguments()[0];
        var enumerableT = typeof(IEnumerable<>).GetGenericArguments()[0];
        var methodT = typeof(Enumerable).GetMethods().First(m => m.Name == "First" && m.GetParameters().Length == 1).GetGenericArguments()[0];
        Assert.IsTrue(TypeIdentity.Equal(listT, listT));
        Assert.IsFalse(TypeIdentity.Equal(listT, enumerableT), "the T of one type is not the T of another");
        Assert.IsFalse(TypeIdentity.Equal(listT, methodT), "!0 is never !!0");
        var prototypeA = PrototypeGenerics.Create(["T"]);
        var prototypeB = PrototypeGenerics.Create(["T"]);
        Assert.IsFalse(TypeIdentity.Equal(prototypeA[0], prototypeB[0]), "same name and position on different owners is not identity");
        var map = new EmitMap(_ => throw new InvalidOperationException());
        map.Add(prototypeA[0].DeclaringMethod!, prototypeB[0].DeclaringMethod!);
        Assert.IsTrue(TypeIdentity.Equal(prototypeA[0], prototypeB[0], map), "a map makes two owners equivalent");
    }

    /// <summary>
    /// Arrays, byrefs, pointers, and instantiations compare by structure.
    /// </summary>
    [TestMethod]
    public void Equal_ConstructedTypes_Structurally()
    {
        Assert.IsTrue(TypeIdentity.Equal(typeof(int[]), typeof(int[])));
        Assert.IsFalse(TypeIdentity.Equal(typeof(int[]), typeof(int).MakeArrayType(1)), "int32[] is not int32[0...]");
        Assert.IsFalse(TypeIdentity.Equal(typeof(int[]), typeof(int[,])));
        Assert.IsTrue(TypeIdentity.Equal(typeof(int).MakeByRefType(), typeof(int).MakeByRefType()));
        Assert.IsFalse(TypeIdentity.Equal(typeof(int).MakeByRefType(), typeof(int).MakePointerType()));
        Assert.IsTrue(TypeIdentity.Equal(typeof(List<int>), typeof(List<int>)));
        Assert.IsFalse(TypeIdentity.Equal(typeof(List<int>), typeof(List<string>)));
        Assert.IsFalse(TypeIdentity.Equal(typeof(List<int>), typeof(IList<int>)));
        var listT = typeof(List<>).GetGenericArguments()[0];
        Assert.IsTrue(TypeIdentity.Equal(typeof(List<>).MakeGenericType(listT), typeof(List<>).MakeGenericType(listT)));
    }
}
