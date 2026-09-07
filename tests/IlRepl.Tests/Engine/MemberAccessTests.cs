using IlRepl.Engine;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Tests for the accessibility rules the REPL enforces on session members, which the runtime
/// does not check for them. Each category is tried from the cell, from the declaring type, from
/// a derived type, and from an unrelated type.
/// </summary>
[TestClass]
public sealed class MemberAccessTests
{
    private static readonly string[] Base =
    [
        ".class public Base {",
        ".field public int32 Pub",
        ".field assembly int32 Asm",
        ".field family int32 Fam",
        ".field famandassem int32 FamAsm",
        ".field famorassem int32 FamOrAsm",
        ".field private int32 Priv",
        ".field int32 Scoped",
        ".field public static int32 SPub",
        ".field family static int32 SFam",
        ".field private static int32 SPriv",
        ".method public instance void .ctor() { ldarg.0; call instance void [System.Runtime]System.Object::.ctor(); ret }",
        ".method family instance int32 FamM() { ldc.i4 1; ret }",
        ".method private instance int32 PrivM() { ldc.i4 2; ret }",
        ".method instance int32 ScopedM() { ldc.i4 3; ret }",
        ".method family static int32 SFamM() { ldc.i4 4; ret }",
        ".method public static int32 SPubM() { ldc.i4 5; ret }",
        "}",
    ];

    private static Session Load(params string[] lines) => IlLines.Load(lines);

    private static string Refused(Session session, string line) => Assert.ThrowsExactly<ReplException>(() => session.AddLine(line)).Message;

    /// <summary>
    /// The cell is in the session's assembly but in no type: assembly categories pass, the rest do not.
    /// </summary>
    [TestMethod]
    public void Cell_SeesAssemblyMembersOnly()
    {
        var session = Load(Base);
        session.AddLine("newobj instance void Base::.ctor()");
        session.AddLine("dup");
        session.AddLine("ldfld int32 Base::Pub");
        session.AddLine("pop");
        session.AddLine("dup");
        session.AddLine("ldfld int32 Base::Asm");
        session.AddLine("pop");
        session.AddLine("dup");
        session.AddLine("ldfld int32 Base::FamOrAsm");
        session.AddLine("pop");
        session.AddLine("dup");
        Assert.Contains("int32 Base::Fam is family; only Base and types derived from it can use it, not the cell", Refused(session, "ldfld int32 Base::Fam"));
        Assert.Contains("is famandassem; only Base and types derived from it", Refused(session, "ldfld int32 Base::FamAsm"));
        Assert.Contains("int32 Base::Priv is private; only Base and the types nested in it can use it, not the cell", Refused(session, "ldfld int32 Base::Priv"));
        Assert.Contains("is privatescope (no access word); only Base's own module can use it, not the cell", Refused(session, "ldfld int32 Base::Scoped"));
        Assert.Contains("is family", Refused(session, "call instance int32 Base::FamM()"));
        Assert.Contains("is private", Refused(session, "call instance int32 Base::PrivM()"));
        Assert.Contains("is privatescope", Refused(session, "call instance int32 Base::ScopedM()"));
        Assert.Contains("is family", Refused(session, "call int32 Base::SFamM()"));
        Assert.Contains("is family", Refused(session, "ldsfld int32 Base::SFam"));
        Assert.Contains("is private", Refused(session, "ldsfld int32 Base::SPriv"));
        Assert.AreEqual(LineOutcome.Instruction, session.AddLine("call int32 Base::SPubM()").Outcome);
        Assert.AreEqual(LineOutcome.Instruction, session.AddLine("ldsfld int32 Base::SPub").Outcome);
    }

    /// <summary>
    /// A session method is in the assembly too, and no type.
    /// </summary>
    [TestMethod]
    public void SessionMethod_IsNotAType()
    {
        var session = Load([.. Base, ".method int32 Read(class Base b) {", "ldarg b"]);
        Assert.Contains("not method Read", Refused(session, "ldfld int32 Base::Priv"));
        Assert.AreEqual(LineOutcome.Instruction, session.AddLine("ldfld int32 Base::Asm").Outcome);
    }

    /// <summary>
    /// The declaring type reaches everything of its own, including privatescope members.
    /// </summary>
    [TestMethod]
    public void DeclaringType_SeesEverything()
    {
        var session = Load(
            ".class public Own {",
            ".field private int32 Priv",
            ".field int32 Scoped",
            ".method instance int32 ScopedM() { ldc.i4 3; ret }",
            ".method public instance int32 Sum(class Own other) {",
            "ldarg.0",
            "ldfld int32 Own::Priv",
            "ldarg other",
            "ldfld int32 Own::Scoped",
            "add",
            "ldarg other",
            "call instance int32 Own::ScopedM()",
            "add",
            "ret",
            "}",
            "}");
        Assert.AreEqual(1, session.TypeCount);
    }

    /// <summary>
    /// A derived type reaches family members through any receiver, as the runtime allows (the
    /// receiver rule of ECMA II.10.5.3 is the verifier's), and never private or privatescope members.
    /// </summary>
    [TestMethod]
    public void DerivedType_ReachesFamilyMembersButNotPrivateOnes()
    {
        var session = Load([.. Base, ".class public Derived extends Base {", ".method public instance int32 Read(class Derived d, class Base b) {"]);
        session.AddLine("ldarg.0");
        Assert.AreEqual(LineOutcome.Instruction, session.AddLine("ldfld int32 Base::Fam").Outcome, "through this");
        session.AddLine("ldarg d");
        Assert.AreEqual(LineOutcome.Instruction, session.AddLine("ldfld int32 Base::FamAsm").Outcome, "through a Derived");
        session.AddLine("add");
        session.AddLine("ldarg b");
        Assert.AreEqual(LineOutcome.Instruction, session.AddLine("ldfld int32 Base::Fam").Outcome, "through a Base");
        session.AddLine("add");
        session.AddLine("ldarg b");
        Assert.Contains("is private; only Base and the types nested in it can use it, not class Derived", Refused(session, "ldfld int32 Base::Priv"));
        Assert.Contains("is privatescope", Refused(session, "ldfld int32 Base::Scoped"));
        Assert.AreEqual(LineOutcome.Instruction, session.AddLine("call instance int32 Base::FamM()").Outcome);
        session.AddLine("add");
        Assert.AreEqual(LineOutcome.Instruction, session.AddLine("call int32 Base::SFamM()").Outcome, "static family members too");
        session.AddLine("add");
        session.AddLine("ldsfld int32 Base::SFam");
        session.AddLine("add");
        session.AddLine("ret");
        session.AddLine("}");
        Assert.AreEqual("end of class Derived", session.AddLine("}").Message);
    }

    /// <summary>
    /// A constructor with family access can be used from a derived type, with call on this or
    /// with newobj, and not from outside.
    /// </summary>
    [TestMethod]
    public void FamilyConstructor_UsableFromDerivedTypesOnly()
    {
        var session = Load(
            ".class public Guarded {",
            ".method family instance void .ctor() { ldarg.0; call instance void [System.Runtime]System.Object::.ctor(); ret }",
            "}",
            ".class public Open extends Guarded {",
            ".method public instance void .ctor() {",
            "ldarg.0");
        Assert.AreEqual(LineOutcome.Instruction, session.AddLine("call instance void Guarded::.ctor()").Outcome);
        session.AddLine("ret");
        session.AddLine("}");
        session.AddLine(".method public static object Make() {");
        Assert.AreEqual(LineOutcome.Instruction, session.AddLine("newobj instance void Guarded::.ctor()").Outcome);
        session.AddLine("ret");
        session.AddLine("}");
        session.AddLine("}");
        Assert.Contains("is family; only Guarded and types derived from it can use it, not the cell", Refused(session, "newobj instance void Guarded::.ctor()"));
    }

    /// <summary>
    /// An unrelated type sees only the assembly categories.
    /// </summary>
    [TestMethod]
    public void UnrelatedType_SeesAssemblyMembersOnly()
    {
        var session = Load([.. Base, ".class public Other {", ".method public instance int32 Read(class Base b) {", "ldarg b"]);
        Assert.Contains("not class Other", Refused(session, "ldfld int32 Base::Fam"));
        Assert.Contains("not class Other", Refused(session, "ldfld int32 Base::Priv"));
        Assert.AreEqual(LineOutcome.Instruction, session.AddLine("ldfld int32 Base::FamOrAsm").Outcome);
    }

    /// <summary>
    /// A nested type reaches the private members of the type it is nested in, and the enclosing
    /// type reaches a nested private type; nothing else does.
    /// </summary>
    [TestMethod]
    public void NestedTypes_ShareTheEnclosingTypesPrivacy()
    {
        var session = Load(
            ".class public Outer {",
            ".field private static int32 Secret",
            ".class nested private Inner {",
            ".method public static int32 Peek() {");
        Assert.AreEqual(LineOutcome.Instruction, session.AddLine("ldsfld int32 Outer::Secret").Outcome, "a nested type sees the enclosing type's private members");
        session.AddLine("ret");
        session.AddLine("}");
        session.AddLine("}");
        session.AddLine(".method public static int32 Use() {");
        Assert.AreEqual(LineOutcome.Instruction, session.AddLine("call int32 Outer/Inner::Peek()").Outcome, "the enclosing type sees its nested private type");
        session.AddLine("ret");
        session.AddLine("}");
        session.AddLine("}");
        Assert.Contains("Outer/Inner is nested private; only Outer and the types nested in it can use it, not the cell", Refused(session, "call int32 Outer/Inner::Peek()"));
        Assert.Contains("is nested private", Refused(session, ".locals init (class Outer/Inner x)"));
        var other = Load(".class public Outer {", ".class nested family Inner { }", "}", ".class public Unrelated {");
        Assert.Contains("Outer/Inner is nested family; only Outer and types derived from it can use it, not class Unrelated", Refused(other, ".field public class Outer/Inner x"));
        var derived = Load(".class public Outer {", ".class nested family Inner { }", "}", ".class public Sub extends Outer {");
        Assert.AreEqual(LineOutcome.Field, derived.AddLine(".field public class Outer/Inner x").Outcome);
    }

    /// <summary>
    /// A member referenced before its header is judged again when the type closes.
    /// </summary>
    [TestMethod]
    public void ForwardReference_IsJudgedAtClose()
    {
        var session = Load(
            ".class public Early {",
            ".method public static int32 Call() { call int32 Early::Later(); ret }",
            ".method private static int32 Later() { ldc.i4 1; ret }",
            "}",
            ".class public Late {",
            ".method public static int32 Call() { call int32 Late::Later(); ret }");
        foreach (var line in IlLines.Expand(".method private static int32 Later() { ldc.i4 1; ret }"))
        {
            session.AddLine(line);
        }

        Assert.AreEqual(1, session.TypeCount, "a private member is fine from its own type");
        Assert.AreEqual("end of class Late", session.AddLine("}").Message);
    }
}
