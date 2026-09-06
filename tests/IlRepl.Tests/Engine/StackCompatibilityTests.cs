using IlRepl.Engine;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Tests for <see cref="StackCompatibility"/>, the rule <c>ret</c> in a method follows.
/// </summary>
[TestClass]
public sealed class StackCompatibilityTests
{
    /// <summary>
    /// Everything that widens to int32 on the stack can be returned as any of those types.
    /// </summary>
    [TestMethod]
    public void CanReturn_Int32Category_AcceptsSmallIntegers()
    {
        Assert.IsTrue(StackCompatibility.CanReturn(typeof(int), typeof(bool)));
        Assert.IsTrue(StackCompatibility.CanReturn(typeof(int), typeof(char)));
        Assert.IsTrue(StackCompatibility.CanReturn(typeof(byte), typeof(int)));
        Assert.IsTrue(StackCompatibility.CanReturn(typeof(int), typeof(uint)));
        Assert.IsTrue(StackCompatibility.CanReturn(typeof(int), typeof(DayOfWeek)));
        Assert.AreEqual(StackCategory.Int32, StackCompatibility.Category(typeof(short)));
    }

    /// <summary>
    /// An int32 is not an int64 and the other way round.
    /// </summary>
    [TestMethod]
    public void CanReturn_Int64Category_RejectsInt32()
    {
        Assert.IsFalse(StackCompatibility.CanReturn(typeof(int), typeof(long)));
        Assert.IsFalse(StackCompatibility.CanReturn(typeof(long), typeof(int)));
        Assert.IsTrue(StackCompatibility.CanReturn(typeof(ulong), typeof(long)));
    }

    /// <summary>
    /// Both floating types share the F category.
    /// </summary>
    [TestMethod]
    public void CanReturn_Float_AcceptsFloat32AndFloat64()
    {
        Assert.IsTrue(StackCompatibility.CanReturn(typeof(double), typeof(float)));
        Assert.IsTrue(StackCompatibility.CanReturn(typeof(float), typeof(double)));
        Assert.IsFalse(StackCompatibility.CanReturn(typeof(int), typeof(double)));
    }

    /// <summary>
    /// A native int slot takes pointers, native ints, and an int32.
    /// </summary>
    [TestMethod]
    public void CanReturn_NativeInt_AcceptsPointersAndInt32()
    {
        Assert.IsTrue(StackCompatibility.CanReturn(typeof(int*), typeof(nint)));
        Assert.IsTrue(StackCompatibility.CanReturn(typeof(nuint), typeof(nint)));
        Assert.IsTrue(StackCompatibility.CanReturn(typeof(int), typeof(nint)));
        Assert.IsFalse(StackCompatibility.CanReturn(typeof(long), typeof(nint)));
        Assert.IsFalse(StackCompatibility.CanReturn(typeof(nint), typeof(int)));
    }

    /// <summary>
    /// References follow assignability, and null fits any reference type.
    /// </summary>
    [TestMethod]
    public void CanReturn_ReferenceTypes_UseAssignability()
    {
        Assert.IsTrue(StackCompatibility.CanReturn(typeof(string), typeof(object)));
        Assert.IsTrue(StackCompatibility.CanReturn(typeof(ArgumentException), typeof(Exception)));
        Assert.IsFalse(StackCompatibility.CanReturn(typeof(object), typeof(string)));
        Assert.IsTrue(StackCompatibility.CanReturn(typeof(int[]), typeof(Array)));
        Assert.IsTrue(StackCompatibility.CanReturn(typeof(NullReferenceMarker), typeof(string)));
    }

    /// <summary>
    /// Value types must match exactly.
    /// </summary>
    [TestMethod]
    public void CanReturn_ValueTypes_RequireExactMatch()
    {
        Assert.IsTrue(StackCompatibility.CanReturn(typeof(decimal), typeof(decimal)));
        Assert.IsFalse(StackCompatibility.CanReturn(typeof(decimal), typeof(Guid)));
        Assert.IsFalse(StackCompatibility.CanReturn(typeof(decimal), typeof(int)));
    }

    /// <summary>
    /// The unknown type is accepted; the JIT decides later.
    /// </summary>
    [TestMethod]
    public void CanReturn_Unknown_IsAccepted()
    {
        Assert.IsTrue(StackCompatibility.CanReturn(null, typeof(int)));
        Assert.IsTrue(StackCompatibility.CanReturn(null, typeof(string)));
    }

    /// <summary>
    /// null cannot stand in for a value type or a pointer.
    /// </summary>
    [TestMethod]
    public void CanReturn_NullMarker_OnlyForReferenceTypes()
    {
        Assert.IsFalse(StackCompatibility.CanReturn(typeof(NullReferenceMarker), typeof(int)));
        Assert.IsFalse(StackCompatibility.CanReturn(typeof(NullReferenceMarker), typeof(nint)));
        Assert.IsFalse(StackCompatibility.CanReturn(typeof(NullReferenceMarker), typeof(Guid)));
    }

    /// <summary>
    /// A value type on the stack is not an object until it is boxed.
    /// </summary>
    [TestMethod]
    public void CanReturn_ValueTypeForObject_IsRejected()
    {
        Assert.IsFalse(StackCompatibility.CanReturn(typeof(int), typeof(object)));
        Assert.IsFalse(StackCompatibility.CanReturn(typeof(Guid), typeof(IFormattable)));
    }
}
