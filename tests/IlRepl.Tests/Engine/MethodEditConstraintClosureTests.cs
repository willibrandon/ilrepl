using System.Reflection;
using IlRepl.Engine;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Generic constraints retain session declarations even when no instruction references the constrained type.
/// </summary>
[TestClass]
public sealed class MethodEditConstraintClosureTests
{
    /// <summary>
    /// An open generic method or owner copies its constraint and executes independently after export.
    /// </summary>
    /// <param name="genericOwner">Whether the constraint belongs to the declaring type.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Edit_ConstraintOnlySessionDependencySurvivesExport(bool genericOwner)
    {
        var session = IlLines.Load(".class interface public abstract IMarker {", "}",
            genericOwner ? ".class public Choice`1<(IMarker) T> {" : ".class public Choice {",
            genericOwner ? ".method public static int32 Read() {" : ".method public static int32 Read<(IMarker) T>() {",
            "ldc.i4.s 41", "ret", "}", "}");
        var edit = session.PrepareEdit(genericOwner ? "int32 Choice`1::Read()" : "int32 Choice::Read<[1]>()", "Copy");
        Assert.IsEmpty(edit.Problems, string.Join("; ", edit.Problems));
        session.CommitEdit(edit.Name, edit.Source.Replace("ldc.i4.s 41", "ldc.i4.s 42", StringComparison.Ordinal));
        var method = Assert.IsInstanceOfType<MethodInfo>(edit.Method);
        AssertCall(method, genericOwner, 42);
        AssertCall(Assert.IsInstanceOfType<MethodInfo>(edit.OriginalMethod), genericOwner, 41);
        var image = AssemblyExporter.Write(session, "ConstraintExport");
        session.Reset();
        var exported = Assembly.Load(image).GetType(method.DeclaringType!.FullName!)!.GetMethod("Read")!;
        AssertCall(exported, genericOwner, 42);
    }

    private static void AssertCall(MethodInfo method, bool genericOwner, int expected)
    {
        var parameter = (genericOwner ? method.DeclaringType!.GetGenericArguments() : method.GetGenericArguments()).Single();
        var contract = parameter.GetGenericParameterConstraints().Single();
        Assert.IsTrue(contract.IsInterface);
        Assert.AreSame(method.Module.Assembly, contract.Assembly);
        var closed = genericOwner ? method.DeclaringType!.MakeGenericType(contract).GetMethod(method.Name)!
            : method.MakeGenericMethod(contract);
        Assert.AreEqual(expected, closed.Invoke(null, null));
        Assert.ThrowsExactly<ArgumentException>(() =>
        {
            if (genericOwner)
            {
                method.DeclaringType!.MakeGenericType(typeof(object));
            }
            else
            {
                method.MakeGenericMethod(typeof(object));
            }
        });
    }
}
