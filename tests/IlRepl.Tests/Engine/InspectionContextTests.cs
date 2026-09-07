using IlRepl.Engine;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Tests for <see cref="Session.InspectionContext"/>: looking a member up never changes what is being written.
/// </summary>
[TestClass]
public sealed class InspectionContextTests
{
    private static Session Open(params string[] lines)
    {
        var session = new Session();
        foreach (var line in lines)
        {
            Ok(session, line);
        }

        return session;
    }

    private static void Ok(Session session, string line)
    {
        // A refused line throws; a returned result is an accepted one.
        Assert.IsNotNull(session.AddLine(line), line);
    }

    /// <summary>
    /// A member that does not exist is an error, and the class still closes afterwards.
    /// </summary>
    [TestMethod]
    public void ResolveMethod_MissingOwnMember_DeclaresNothing()
    {
        var session = Open(".class public C {", ".method public static void M() {");
        Assert.Throws<ReplException>(() => MemberResolver.ResolveMethod("void C::Missing()", session.InspectionContext, false));
        Ok(session, "ret");
        Ok(session, "}");
        Ok(session, "}");
        Assert.Contains("C", session.ToIlAsm());
    }

    /// <summary>
    /// A nested type named by path in a framework overload is not forward-declared either.
    /// </summary>
    [TestMethod]
    public void ResolveMethod_MissingNestedTypeInParameter_DeclaresNothing()
    {
        var session = Open(".class public C {", ".method public static void M() {");
        Assert.Throws<ReplException>(() => MemberResolver.ResolveMethod("void [System.Console]System.Console::WriteLine(class C/Missing)", session.InspectionContext, false));
        Ok(session, "ret");
        Ok(session, "}");
        Ok(session, "}");
    }

    /// <summary>
    /// While a family is being redefined, its names resolve to the accepted generation.
    /// </summary>
    [TestMethod]
    public void ResolveMethod_PendingReplacement_UsesCommittedGeneration()
    {
        var session = Open(".class public Point {", ".method public instance int32 Sum() {", "ldc.i4 7", "ret", "}", "}");
        Ok(session, ".class public Point {");
        Ok(session, ".method public instance int32 Sum() {");
        var resolved = MemberResolver.ResolveMethod("instance int32 Point::Sum()", session.InspectionContext, false);
        Assert.IsNotNull(resolved.Method);
        Assert.IsNull(resolved.Declared);
        Assert.IsFalse(resolved.Method.DeclaringType is System.Reflection.Emit.TypeBuilder);
        Assert.IsNotNull(resolved.Method.GetMethodBody());

        // The editor's own context still sees the prototype.
        var editing = MemberResolver.ResolveMethod("instance int32 Point::Sum()", session.State.Context, false);
        Assert.IsNotNull(editing.Declared);
        Ok(session, "ldc.i4 8");
        Ok(session, "ret");
        Ok(session, "}");
        Ok(session, "}");
    }

    /// <summary>
    /// A class with no accepted generation is not in the view at all.
    /// </summary>
    [TestMethod]
    public void ResolveMethod_NewOpenClass_IsNotVisible()
    {
        var session = Open(".class public Fresh {", ".method public static int32 M() {", "ldc.i4 1", "ret", "}");
        var error = Assert.Throws<ReplException>(() => MemberResolver.ResolveMethod("int32 Fresh::M()", session.InspectionContext, false));
        Assert.Contains("Fresh", error.Message);
        Ok(session, "}");
        Assert.IsNotNull(MemberResolver.ResolveMethod("int32 Fresh::M()", session.InspectionContext, false).Method);
    }

    /// <summary>
    /// Session methods and the cell's generic parameters are in the view, and lookups leave the transcript alone.
    /// </summary>
    [TestMethod]
    public void InspectionContext_SeesSessionMethods_AndChangesNothing()
    {
        var session = Open(".method int32 Two() {", "ldc.i4 2", "ret", "}", "ldc.i4 1");
        var il = session.ToIlAsm();
        var resolved = MemberResolver.ResolveMethod("Two", session.InspectionContext, false);
        Assert.IsNotNull(resolved.Definition);
        Assert.Throws<ReplException>(() => MemberResolver.ResolveMethod("Nope", session.InspectionContext, false));
        Assert.AreEqual(il, session.ToIlAsm());
        Assert.IsTrue(session.InspectionContext.Inspecting);
        Assert.IsNull(session.InspectionContext.Types.Forward);
    }
}
