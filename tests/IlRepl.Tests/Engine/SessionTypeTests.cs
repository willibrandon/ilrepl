using System.Reflection;
using IlRepl.Engine;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Tests for <see cref="Session"/> with <c>.class</c> blocks: opening, members, nesting, undo,
/// abandoning, and closing a family.
/// </summary>
[TestClass]
public sealed class SessionTypeTests
{
    private static readonly string[] Point =
    [
        ".class public sequential ansi sealed Point extends [System.Runtime]System.ValueType {",
        ".field public int32 X",
        ".field public int32 Y",
        ".method public instance void .ctor(int32 x, int32 y) {",
        "ldarg.0",
        "ldarg x",
        "stfld int32 Point::X",
        "ldarg.0",
        "ldarg y",
        "stfld int32 Point::Y",
        "ret",
        "}",
        ".method public instance int32 Sum() {",
        "ldarg.0",
        "ldfld int32 Point::X",
        "ldarg.0",
        "ldfld int32 Point::Y",
        "add",
        "ret",
        "}",
        "}",
    ];

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
    /// A header opens a type: the path is reported, the state is still the cell, nothing is defined yet.
    /// </summary>
    [TestMethod]
    public void AddLine_ClassHeader_OpensType()
    {
        var session = new Session();
        var result = session.AddLine(Point[0]);
        Assert.AreEqual(LineOutcome.TypeStart, result.Outcome);
        Assert.AreEqual("struct Point", result.Message);
        Assert.AreEqual("Point", session.OpenType);
        Assert.IsNull(session.OpenMethod);
        Assert.IsFalse(session.State.IsMethod);
        Assert.IsEmpty(session.Types);
        Assert.AreEqual(0, session.Submissions);
    }

    /// <summary>
    /// The brace may follow the header on its own line, and an empty type closes with <c>{ }</c>.
    /// </summary>
    [TestMethod]
    public void AddLine_BraceForms()
    {
        var session = Load(".class public Later", "{", "}");
        Assert.HasCount(1, session.Types);
        Assert.IsNull(session.OpenType);
        var empty = Load(".class public Empty { }");
        Assert.HasCount(1, empty.Types);
        Assert.AreEqual(1, empty.Submissions);
    }

    /// <summary>
    /// Fields and members are noted as they arrive, with this at argument 0 of an instance member.
    /// </summary>
    [TestMethod]
    public void AddLine_FieldsAndMembers_AreNotedAndTyped()
    {
        var session = new Session();
        session.AddLine(Point[0]);
        Assert.AreEqual("field public int32 X", session.AddLine(".field public int32 X").Message);
        Assert.AreEqual(LineOutcome.Field, session.AddLine(".field public int32 Y").Outcome);
        var header = session.AddLine(".method public instance int32 Sum() {");
        Assert.AreEqual(LineOutcome.MethodStart, header.Outcome);
        Assert.AreEqual("method instance int32 Sum()", header.Message);
        Assert.AreEqual("Sum", session.OpenMethod!.Name);
        Assert.IsTrue(session.State.IsMember);
        Assert.HasCount(1, session.State.Arguments);
        Assert.IsTrue(session.State.Arguments[0].Type.IsByRef, "this is a reference in a struct member");
        session.AddLine("ldarg.0");
        Assert.AreEqual("[Point&]", session.State.Stack.Render());
        session.AddLine("ldfld int32 Point::X");
        Assert.AreEqual("[int32]", session.State.Stack.Render());
        Assert.AreEqual("end of method Sum", session.AddLine("}").Message);
        Assert.IsNull(session.OpenMethod);
        Assert.AreEqual("Point", session.OpenType);
    }

    /// <summary>
    /// Closing the outermost brace commits the family as one submission with its members.
    /// </summary>
    [TestMethod]
    public void AddLine_ClassClose_CommitsFamily()
    {
        var session = new Session();
        LineResult last = new(LineOutcome.Empty, null, null);
        foreach (var line in Point)
        {
            last = session.AddLine(line);
        }

        Assert.AreEqual(LineOutcome.TypeEnd, last.Outcome);
        Assert.AreEqual("end of struct Point", last.Message);
        Assert.IsNull(session.OpenType);
        Assert.HasCount(1, session.Types);
        Assert.AreEqual(1, session.Submissions, "the whole family is one submission");
        var declaration = session.Types[0].Declaration;
        Assert.AreEqual(TypeKind.Struct, declaration.Kind);
        Assert.AreEqual("Point", declaration.FullName);
        Assert.HasCount(2, declaration.Fields);
        Assert.HasCount(2, declaration.Methods);
        Assert.IsTrue(declaration.Methods[0].IsConstructor);
        Assert.AreEqual("instance int32 Sum()", declaration.Methods[1].Describe());
        Assert.HasCount(Point.Length - 2, declaration.Lines);
    }

    /// <summary>
    /// A nested class reports its path, closes back into the outer block, and is committed with it.
    /// </summary>
    [TestMethod]
    public void AddLine_NestedClass_TracksPath()
    {
        var session = Load(".class public Outer {", ".field public static int32 Count");
        Assert.AreEqual("class Outer/Inner", session.AddLine(".class nested public Inner {").Message);
        Assert.AreEqual("Outer/Inner", session.OpenType);
        session.AddLine(".method public static int32 Bump() {");
        session.AddLine("ldsfld int32 Outer::Count");
        session.AddLine("ldc.i4 1");
        session.AddLine("add");
        session.AddLine("dup");
        session.AddLine("stsfld int32 Outer::Count");
        session.AddLine("ret");
        session.AddLine("}");
        Assert.AreEqual("end of class Outer/Inner", session.AddLine("}").Message);
        Assert.AreEqual("Outer", session.OpenType);
        Assert.AreEqual("end of class Outer", session.AddLine("}").Message);
        Assert.HasCount(1, session.Types);
        Assert.AreEqual(2, session.TypeCount);
        Assert.AreEqual("Outer/Inner", session.Types[0].Declaration.NestedTypes[0].FullName);
    }

    /// <summary>
    /// A member may call a member declared later, and a nested type may be named before its
    /// declaration; both are settled when the family closes.
    /// </summary>
    [TestMethod]
    public void AddLine_ForwardReferences_AreSettledAtClose()
    {
        var session = Load(
            ".class public Outer {",
            ".method public static int32 A() {", "call int32 Outer::B()", "ret", "}",
            ".field public static valuetype Outer/Inner Slot",
            ".method public static int32 B() {", "ldc.i4 3", "ret", "}",
            ".class nested public Inner extends System.ValueType {", "}");
        Assert.AreEqual("end of class Outer", session.AddLine("}").Message);
        Assert.HasCount(1, session.Types);
    }

    /// <summary>
    /// A family that closes with a forward reference still undeclared is refused and stays open.
    /// </summary>
    [TestMethod]
    public void AddLine_UndeclaredForwardReference_RefusesClose()
    {
        var session = Load(".class public Outer {", ".method public static int32 A() {", "call int32 Outer::B()", "ret", "}");
        Assert.Contains("referenced and never declared", Assert.ThrowsExactly<ReplException>(() => session.AddLine("}")).Message);
        Assert.AreEqual("Outer", session.OpenType);
        var kind = Load(".class public Outer {", ".field public static valuetype Outer/Inner Slot");
        Assert.Contains("referenced as a valuetype", Assert.ThrowsExactly<ReplException>(() => kind.AddLine(".class nested public Inner {")).Message);
    }

    /// <summary>
    /// Undo takes back the last line of the family and replays the rest; on the header it abandons the family.
    /// </summary>
    [TestMethod]
    public void Undo_InsideClass_ReplaysThenAbandons()
    {
        var session = Load(Point[0], ".field public int32 X", ".field public int32 Y");
        Assert.IsTrue(session.Undo());
        Assert.AreEqual("Point", session.OpenType);
        Assert.AreEqual("field public int32 Y", session.AddLine(".field public int32 Y").Message);
        session.AddLine(".method public instance int32 Sum() {");
        session.AddLine("ldarg.0");
        Assert.IsTrue(session.Undo());
        Assert.AreEqual("Sum", session.OpenMethod!.Name);
        Assert.AreEqual("[]", session.State.Stack.Render());
        Assert.IsTrue(session.Undo());
        Assert.IsNull(session.OpenMethod);
        Assert.AreEqual("Point", session.OpenType);
        Assert.IsTrue(session.Undo());
        Assert.IsTrue(session.Undo());
        Assert.IsTrue(session.Undo(), "undoing the header abandons the family");
        Assert.IsNull(session.OpenType);
        Assert.IsEmpty(session.Types);
        Assert.AreEqual(0, session.Submissions);
    }

    /// <summary>
    /// Abandoning leaves the cell, the methods, and the accepted types alone.
    /// </summary>
    [TestMethod]
    public void AbandonType_KeepsEverythingElse()
    {
        var session = Load(".method int32 One() {", "ldc.i4 1", "ret", "}", "ldc.i4 5");
        session.AddLine(".class public Half {");
        session.AddLine(".field public int32 X");
        Assert.IsTrue(session.AbandonType());
        Assert.IsNull(session.OpenType);
        Assert.HasCount(1, session.Methods);
        Assert.AreEqual("[int32]", session.Cell.Stack.Render());
        Assert.IsFalse(session.AbandonType());
    }

    /// <summary>
    /// The cell cannot run or save while a class is open.
    /// </summary>
    [TestMethod]
    public void Run_WhileClassOpen_Throws()
    {
        var session = Load("ldc.i4 1", Point[0]);
        Assert.Contains("class Point is still open", Assert.ThrowsExactly<ReplException>(() => session.Run()).Message);
    }

    /// <summary>
    /// Lines that belong elsewhere are refused with a pointer to where they go.
    /// </summary>
    [TestMethod]
    public void AddLine_Placement_IsChecked()
    {
        var session = Load(Point[0]);
        Assert.Contains("belongs in a method body", Assert.ThrowsExactly<ReplException>(() => session.AddLine(".locals init (int32 i)")).Message);
        Assert.Contains("instructions belong in a method body", Assert.ThrowsExactly<ReplException>(() => session.AddLine("ldc.i4 1")).Message);
        session.AddLine(".method public instance int32 Sum() {");
        Assert.Contains("not allowed inside a method", Assert.ThrowsExactly<ReplException>(() => session.AddLine(".field public int32 Z")).Message);
        Assert.Contains("cannot be declared inside a method", Assert.ThrowsExactly<ReplException>(() => session.AddLine(".class nested public Inner {")).Message);
        session.AddLine("ldc.i4 1");
        session.AddLine("ret");
        session.AddLine("}");
        session.AddLine("}");
        Assert.Contains("belongs inside a .class block", Assert.ThrowsExactly<ReplException>(() => session.AddLine(".field public int32 Z")).Message);
    }

    /// <summary>
    /// Duplicate fields and methods, and an abstract method on a concrete class, are refused where typed.
    /// </summary>
    [TestMethod]
    public void AddLine_DuplicatesAndAbstract_AreRefused()
    {
        var session = Load(".class public Shape {", ".field public int32 X");
        Assert.Contains("already declared", Assert.ThrowsExactly<ReplException>(() => session.AddLine(".field public int32 X")).Message);
        Assert.Contains("add 'abstract' to the .class header", Assert.ThrowsExactly<ReplException>(() => session.AddLine(".method public abstract virtual instance int32 Area() { }")).Message);
        session.AddLine(".method public instance int32 Area() {");
        session.AddLine("ldc.i4 1");
        session.AddLine("ret");
        session.AddLine("}");
        Assert.Contains("already declared", Assert.ThrowsExactly<ReplException>(() => session.AddLine(".method public instance int32 Area() {")).Message);
    }

    /// <summary>
    /// An abstract member has no body; an interface member is abstract virtual or a default implementation.
    /// </summary>
    [TestMethod]
    public void AddLine_AbstractAndInterfaceMembers()
    {
        var session = Load(".class interface public abstract IArea {");
        Assert.AreEqual("method instance float64 Area(); end of method Area", session.AddLine(".method public abstract virtual instance float64 Area() { }").Message);
        session.AddLine(".method public virtual instance int32 Read() {");
        session.AddLine("ldc.i4 3");
        session.AddLine("ret");
        session.AddLine("}");
        Assert.AreEqual("end of interface IArea", session.AddLine("}").Message);
        var declaration = session.Types[0].Declaration;
        Assert.IsTrue(declaration.Methods[0].IsAbstract);
        Assert.IsNull(declaration.Methods[0].Body);
        Assert.IsNotNull(declaration.Methods[1].Body);

        var open = Load(".class public abstract Shape {", ".method public abstract virtual instance int32 Area() {");
        Assert.Contains("has no body", Assert.ThrowsExactly<ReplException>(() => open.AddLine("ldc.i4 1")).Message);
        Assert.AreEqual("end of method Area", open.AddLine("}").Message);
    }

    /// <summary>
    /// An initonly field is stored through this in a constructor and refused elsewhere.
    /// </summary>
    [TestMethod]
    public void AddLine_InitOnlyStores_AreChecked()
    {
        var session = Load(".class public Box {", ".field public initonly int32 V", ".field public static initonly int32 S",
            ".method public instance void .ctor(int32 v) {", "ldarg.0", "ldarg v", "stfld int32 Box::V", "ret", "}");
        session.AddLine(".method public instance void Set(int32 v) {");
        session.AddLine("ldarg.0");
        session.AddLine("ldarg v");
        Assert.Contains("initonly; it can only be stored through this", Assert.ThrowsExactly<ReplException>(() => session.AddLine("stfld int32 Box::V")).Message);
        session.AddLine("pop");
        session.AddLine("pop");
        session.AddLine("ldc.i4 1");
        Assert.Contains("static initonly field", Assert.ThrowsExactly<ReplException>(() => session.AddLine("stsfld int32 Box::S")).Message);
        session.AddLine("pop");
        session.AddLine("ret");
        session.AddLine("}");
        session.AddLine(".method static void .cctor() {");
        session.AddLine("ldc.i4 1");
        Assert.AreEqual(LineOutcome.Instruction, session.AddLine("stsfld int32 Box::S").Outcome);
        session.AddLine("ret");
        session.AddLine("}");
        session.AddLine(".method public instance void Other(class Box other) {");
        session.AddLine("ldarg other");
        session.AddLine("ldc.i4 1");
        Assert.Contains("through this", Assert.ThrowsExactly<ReplException>(() => session.AddLine("stfld int32 Box::V")).Message, "a receiver that is not this is refused even in the declaring type");
    }

    /// <summary>
    /// <c>.param</c> sets a default without making the parameter optional, and attributes attach to what precedes them.
    /// </summary>
    [TestMethod]
    public void AddLine_ParamAndCustom()
    {
        var session = Load(".class public Opts {", ".method public static int32 M(int32 x) {");
        Assert.AreEqual("param 1 = int32(7)", session.AddLine(".param [1] = int32(7)").Message);
        Assert.Contains("ObsoleteAttribute", session.AddLine(".custom instance void [System.Runtime]System.ObsoleteAttribute::.ctor(string) = { string('old') }").Message!);
        session.AddLine("ldarg x");
        session.AddLine("ret");
        session.AddLine("}");
        session.AddLine("}");
        var method = session.Types[0].Declaration.Methods[0];
        var parameter = method.Signature.Parameters[0];
        Assert.IsTrue(parameter.HasDefault);
        Assert.AreEqual(7, parameter.DefaultValue);
        Assert.IsFalse(parameter.Attributes.HasFlag(System.Reflection.ParameterAttributes.Optional));
        Assert.HasCount(1, parameter.CustomAttributes);
        Assert.AreEqual("old", parameter.CustomAttributes[0].FixedArguments[0]);
    }

    /// <summary>
    /// A property names its accessors, which are resolved when the type closes.
    /// </summary>
    [TestMethod]
    public void AddLine_Property_ResolvesAccessors()
    {
        var session = Load(".class public Sized {",
            ".method public instance int32 get_Length() {", "ldc.i4 4", "ret", "}",
            ".property instance int32 Length() {", ".get instance int32 Sized::get_Length()", "}", "}");
        var property = session.Types[0].Declaration.Properties[0];
        Assert.AreEqual("Length", property.Name);
        Assert.AreEqual("get_Length", property.Getter!.Name);
        Assert.IsNull(property.Setter);
        var missing = Load(".class public Sized {", ".property instance int32 Length() {", ".get instance int32 get_Length()", "}");
        Assert.Contains("does not declare", Assert.ThrowsExactly<ReplException>(() => missing.AddLine("}")).Message);
    }

    /// <summary>
    /// Reset drops the types and the open family.
    /// </summary>
    [TestMethod]
    public void Reset_DropsTypes()
    {
        var session = Load(".class public Gone { }", ".class public Half {");
        session.Reset();
        Assert.IsEmpty(session.Types);
        Assert.IsNull(session.OpenType);
        Assert.AreEqual(0, session.TypeCount);
    }

    /// <summary>
    /// Methods that differ only in generic arity are distinct overloads.
    /// </summary>
    [TestMethod]
    public void AddLine_GenericArity_DistinguishesOverloads()
    {
        var session = Load(".class public Over {", ".method public static int32 F() {", "ldc.i4 1", "ret", "}");
        Assert.AreEqual("method static int32 F<T>()", session.AddLine(".method public static int32 F<T>() {").Message);
        session.AddLine("ldc.i4 2");
        session.AddLine("ret");
        session.AddLine("}");
        Assert.AreEqual("end of class Over", session.AddLine("}").Message);
        Assert.HasCount(2, session.Types[0].RuntimeType!.GetMethods().Where(m => m.Name == "F").ToList());
    }

    /// <summary>
    /// A member inherited from a base is found on the open type, whether the base is loaded or
    /// still being written.
    /// </summary>
    [TestMethod]
    public void AddLine_InheritedMembers_ResolveOnOpenTypes()
    {
        var session = Load(".class public Base {", ".field public int32 N", ".method public instance int32 F() {", "ldc.i4 7", "ret", "}", "}",
            ".class public Derived extends Base {", ".method public instance int32 G() {", "ldarg.0");
        Assert.AreEqual(LineOutcome.Instruction, session.AddLine("call instance int32 Derived::F()").Outcome);
        session.AddLine("ldarg.0");
        Assert.AreEqual(LineOutcome.Instruction, session.AddLine("ldfld int32 Derived::N").Outcome);
        session.AddLine("add");
        session.AddLine("ret");
        session.AddLine("}");
        Assert.AreEqual("end of class Derived", session.AddLine("}").Message);
        var open = Load(".class public Outer {", ".class nested public Base {", ".method public instance int32 F() {", "ldc.i4 3", "ret", "}", "}", ".class nested public Derived extends Outer/Base {", ".method public instance int32 G() {", "ldarg.0");
        Assert.AreEqual(LineOutcome.Instruction, open.AddLine("call instance int32 Outer/Derived::F()").Outcome);
    }

    /// <summary>
    /// Abandoning a member with .clear removes it from the family, so it can be declared again
    /// and a later replay does not bring it back.
    /// </summary>
    [TestMethod]
    public void AbandonMethod_InsideClass_RemovesTheMember()
    {
        var session = Load(".class public Box {", ".field public int32 V", ".method public instance int32 Get() {", "ldc.i4 1");
        Assert.IsTrue(session.AbandonMethod());
        Assert.AreEqual("Box", session.OpenType);
        Assert.IsNull(session.OpenMethod);
        Assert.AreEqual("method instance int32 Get()", session.AddLine(".method public instance int32 Get() {").Message);
        session.AddLine("ldc.i4 2");
        session.AddLine("ret");
        session.AddLine("}");
        session.AddLine("}");
        Assert.HasCount(1, session.Types[0].Declaration.Methods);
        Assert.AreEqual("ldc.i4 2", session.Types[0].Declaration.Methods[0].BodyLines[0]);
    }

    /// <summary>
    /// An interface with inline abstract members replays cleanly, since each header is recorded once.
    /// </summary>
    [TestMethod]
    public void Undo_AfterInlineAbstractMembers_Replays()
    {
        var session = Load(".class interface public abstract IPair {", ".method public abstract virtual instance int32 F() { }", ".method public abstract virtual instance int32 G() { }");
        Assert.IsTrue(session.Undo());
        Assert.AreEqual("IPair", session.OpenType);
        Assert.AreEqual("method instance int32 G(); end of method G", session.AddLine(".method public abstract virtual instance int32 G() { }").Message);
        Assert.AreEqual("end of interface IPair", session.AddLine("}").Message);
    }

    /// <summary>
    /// A nested header the checks refuse leaves nothing behind, so the corrected header is accepted.
    /// </summary>
    [TestMethod]
    public void AddLine_RejectedNestedHeader_LeavesNoTrace()
    {
        var session = Load(".class public A {");
        Assert.Contains("sealed", Assert.ThrowsExactly<ReplException>(() => session.AddLine(".class nested public B extends string {")).Message);
        Assert.AreEqual("A", session.OpenType);
        Assert.AreEqual("class A/B; end of class A/B", session.AddLine(".class nested public B { }").Message);
        Assert.AreEqual("end of class A", session.AddLine("}").Message);
        Assert.AreEqual(2, session.TypeCount);
    }

    /// <summary>
    /// Attributes after .param [0] belong to the return value, and every attribute after
    /// .param [N] belongs to that parameter.
    /// </summary>
    [TestMethod]
    public void AddLine_ParamAttributes_TargetReturnAndParameters()
    {
        var session = Load(".class public Attr {",
            ".method public static int32 M(int32 a) {",
            ".param [0]", ".custom instance void [System.Runtime]System.ObsoleteAttribute::.ctor(string) = { string('ret') }",
            ".param [1]", ".custom instance void [System.Runtime]System.ObsoleteAttribute::.ctor(string) = { string('one') }",
            ".custom instance void [System.Runtime]System.Diagnostics.ConditionalAttribute::.ctor(string) = { string('two') }",
            "ldarg a", "ret", "}", "}");
        var method = session.Types[0].RuntimeType!.GetMethod("M")!;
        Assert.AreEqual("ret", method.ReturnParameter.GetCustomAttribute<ObsoleteAttribute>()!.Message);
        Assert.IsEmpty(method.GetCustomAttributes(false));
        var parameter = method.GetParameters()[0];
        Assert.AreEqual("one", parameter.GetCustomAttribute<ObsoleteAttribute>()!.Message);
        Assert.AreEqual("two", parameter.GetCustomAttribute<System.Diagnostics.ConditionalAttribute>()!.ConditionString);
        Assert.HasCount(1, session.Types[0].Declaration.Methods[0].Signature.ReturnCustomAttributes);
    }

    /// <summary>
    /// Indexers overload by their parameters.
    /// </summary>
    [TestMethod]
    public void AddLine_Indexers_OverloadByParameters()
    {
        var session = Load(".class public Table {",
            ".method public specialname instance int32 get_Item(int32 i) {", "ldarg i", "ret", "}",
            ".method public specialname instance int32 get_Item(string s) {", "ldc.i4 0", "ret", "}",
            ".property instance int32 Item(int32) {", ".get instance int32 Table::get_Item(int32)", "}");
        Assert.AreEqual("property Item", session.AddLine(".property instance int32 Item(string) {").Message);
        session.AddLine(".get instance int32 Table::get_Item(string)");
        session.AddLine("}");
        Assert.AreEqual("end of class Table", session.AddLine("}").Message);
        Assert.HasCount(2, session.Types[0].RuntimeType!.GetProperties());
    }

    /// <summary>
    /// A call picks the overload by generic arity, so F() is not ambiguous with F&lt;T&gt;().
    /// </summary>
    [TestMethod]
    public void AddLine_CallsPickOverloadsByArity()
    {
        var session = IlLines.Load(".class public Over {", ".method public static int32 F() { ldc.i4 1; ret }", ".method public static int32 F<T>() { ldc.i4 2; ret }",
            ".method public static int32 Both() { call int32 Over::F(); call int32 Over::F<string>(); add; ret }", "}");
        Assert.AreEqual(3, session.Types[0].RuntimeType!.GetMethod("Both")!.Invoke(null, null));
    }

    /// <summary>
    /// Properties with the same name and parameters but different types are distinct, as the
    /// CLI's property signature includes the type.
    /// </summary>
    [TestMethod]
    public void AddLine_Properties_DifferByType()
    {
        var session = IlLines.Load(".class public Table {",
            ".method public specialname instance int32 get_Item(int32 i) { ldarg i; ret }",
            ".method public specialname instance string get_Text(int32 i) { ldnull; ret }",
            ".property instance int32 Item(int32) {", ".get instance int32 Table::get_Item(int32)", "}",
            ".property instance string Item(int32) {", ".get instance string Table::get_Text(int32)", "}",
            "}");
        var properties = session.Types[0].RuntimeType!.GetProperties();
        Assert.HasCount(2, properties);
        Assert.IsTrue(properties.Any(p => p.PropertyType == typeof(int)) && properties.Any(p => p.PropertyType == typeof(string)));
    }

    /// <summary>
    /// A method's generic arguments substitute by position, so a leading parameter the
    /// signature never mentions does not shift the others.
    /// </summary>
    [TestMethod]
    public void AddLine_GenericArguments_SubstituteByPosition()
    {
        var session = IlLines.Load(".class public Pick {", ".method public static !!1 Id<TUnused, T>(!!1 v) { ldarg v; ret }",
            ".method public static string Use() { ldstr \"ok\"; call !!1 Pick::Id<int32, string>(!!1); ret }", "}");
        Assert.AreEqual("ok", session.Types[0].RuntimeType!.GetMethod("Use")!.Invoke(null, null));
    }
}
