using System.Runtime.Loader;
using IlRepl.Engine;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Constructed types retain custom modifiers nested in their generic arguments.
/// </summary>
[TestClass]
public sealed class ConstructedTypeModifierTests
{
    private const string Marker = "[System.Runtime]System.Runtime.CompilerServices.IsVolatile";
    private const string AnnotatedInt = $"int32 modreq({Marker})";
    private const string List = $"class [System.Collections]System.Collections.Generic.List`1<{AnnotatedInt}>";
    private const string Box = $"class Box`1<{AnnotatedInt}>";

    /// <summary>
    /// A field keeps a modifier nested in its generic argument through live metadata, listing, and export.
    /// </summary>
    [TestMethod]
    public void Field_NestedGenericModifier_PreservesItsMetadataShape()
    {
        var session = IlLines.Load(
            ".class public Holder {",
            $".field public {List} Items",
            "}");

        AssertField(session.Types.Single().Definition!.Image!);
        Assert.Contains("List<int32 modreq(IsVolatile)>",
            session.Types.Single().Declaration.Fields.Single().Describe());
        var il = session.ToIlAsm();
        Assert.Contains($".field public {List} Items", il);
        AssertField(IlasmLocator.Assemble(il));
        AssertField(AssemblyExporter.Write(session, "nested-generic-field-modifier"));
    }

    /// <summary>
    /// Method and field references keep the annotated owner and substituted signature everywhere.
    /// </summary>
    [TestMethod]
    public void MemberReferences_NestedGenericModifier_PreserveOwnerAndSubstitution()
    {
        var session = BoxSession(Marker);
        var method = session.Methods.Single(candidate => candidate.Signature.Name == "Read");

        Assert.AreEqual(7, method.Version.Body.Invoke(null, null));
        AssertMemberReferences(method.Version.Definition.Image!, "IlRepl.Cell", "Read");
        var il = session.ToIlAsm();
        Assert.Contains($"stsfld !0 {Box}::Value", il);
        Assert.Contains($"call !0 {Box}::Identity(!0)", il);
        var assembled = IlasmLocator.Assemble(il);
        AssertMemberReferences(assembled, "IlRepl.Cell", "Read");
        AssertRuns(assembled);

        var exported = AssemblyExporter.Write(session, "nested-generic-member-modifier");
        AssertMemberReferences(exported, "IlRepl.Cell", "Read");
        AssertRuns(exported);
    }

    /// <summary>
    /// A modifier type used only inside a constructed member owner rebuilds and remaps its caller.
    /// </summary>
    [TestMethod]
    public void MemberOwner_ModifierTypeReplacement_RebuildsAndExportsItsCaller()
    {
        var session = BoxSession("Marker", ".class public Marker { }");
        var previous = session.Methods.Single(method => method.Signature.Name == "Read").Version;

        session.AddLine(".class public Marker {");
        session.AddLine(".field public int32 Generation");
        var result = session.AddLine("}");

        Assert.Contains("rebuilt method Read", result.Message!);
        var current = session.Methods.Single(method => method.Signature.Name == "Read").Version;
        Assert.AreNotSame(previous, current);
        Assert.AreEqual(7, current.Body.Invoke(null, null));
        AssertMemberReferences(current.Definition.Image!, "IlRepl.Cell", "Read", "Marker");
        var exported = AssemblyExporter.Write(session, "nested-generic-owner-replacement");
        AssertMemberReferences(exported, "IlRepl.Cell", "Read", "Marker");
        AssertRuns(exported);
    }

    /// <summary>
    /// A private session modifier nested in a framework generic owner remains inaccessible to a cell.
    /// </summary>
    [TestMethod]
    public void MemberOwner_NestedPrivateModifier_IsRejected()
    {
        var session = IlLines.Load(
            ".class public Outer {",
            ".class nested private Marker { }",
            "}",
            ".class public Box<T> {",
            ".method public static !T Identity(!T value) { ldarg value; ret }",
            "}");

        var exception = Assert.ThrowsExactly<ReplException>(() => session.AddLine(
            "call int32 modreq(Outer/Marker) class Box`1<int32 modreq(Outer/Marker)>::Identity(int32 modreq(Outer/Marker))"));

        Assert.Contains("Outer/Marker is nested private", exception.Message);
    }

    /// <summary>
    /// A field reference must name the field's exact substituted type, including modifier order and kind.
    /// </summary>
    [TestMethod]
    public void FieldReference_DifferentNestedModifier_IsRejectedWithBothTypes()
    {
        var session = IlLines.Load(
            ".class public Box<T> {",
            ".field public static !T Value",
            "}");
        var owner = $"class Box`1<{AnnotatedInt}>";
        var written = $"int32 modopt({Marker})";

        var exception = Assert.ThrowsExactly<ReplException>(() =>
            session.AddLine($"ldsfld {written} {owner}::Value"));

        Assert.Contains("field Box<int32>::Value has type int32 modreq(IsVolatile)", exception.Message);
        Assert.Contains("not int32 modopt(IsVolatile)", exception.Message);
    }

    private static Session BoxSession(string modifier, params string[] prelude)
    {
        var annotated = $"int32 modreq({modifier})";
        var box = $"class Box`1<{annotated}>";
        return IlLines.Load([
            .. prelude,
            ".class public Box<T> {",
            ".field public static !T Value",
            ".method public static !T Identity(!T value) { ldarg value; ret }",
            "}",
            ".method public static int32 Read() {",
            "ldc.i4.7",
            $"stsfld {annotated} {box}::Value",
            $"ldsfld {annotated} {box}::Value",
            $"call {annotated} {box}::Identity({annotated})",
            "ret",
            "}",
        ]);
    }

    private static void AssertField(byte[] image)
    {
        using var assembly = AssemblyDefinition.ReadAssembly(new MemoryStream(image));
        var type = assembly.MainModule.GetType("Holder").Fields.Single().FieldType;
        AssertAnnotatedArgument(type, "IsVolatile");
    }

    private static void AssertMemberReferences(byte[] image, string typeName, string methodName, string modifier = "IsVolatile")
    {
        using var assembly = AssemblyDefinition.ReadAssembly(new MemoryStream(image));
        var method = assembly.MainModule.GetType(typeName).Methods.Single(candidate => candidate.Name == methodName);
        var field = (FieldReference)method.Body.Instructions.Single(instruction => instruction.OpCode == OpCodes.Stsfld).Operand;
        var called = (MethodReference)method.Body.Instructions.Single(instruction => instruction.OpCode == OpCodes.Call).Operand;
        AssertAnnotatedArgument(field.DeclaringType, modifier);
        AssertGenericParameter(field.FieldType);
        AssertAnnotatedArgument(called.DeclaringType, modifier);
        AssertGenericParameter(called.ReturnType);
        AssertGenericParameter(called.Parameters.Single().ParameterType);
    }

    private static void AssertAnnotatedArgument(TypeReference type, string modifier)
    {
        var constructed = type as GenericInstanceType
            ?? throw new AssertFailedException($"expected a constructed type, found {type.GetType().Name}");
        AssertAnnotated(constructed.GenericArguments.Single(), modifier);
    }

    private static void AssertAnnotated(TypeReference type, string modifier)
    {
        var annotated = type as RequiredModifierType
            ?? throw new AssertFailedException($"expected modreq, found {type.GetType().Name}");
        Assert.AreEqual(modifier, annotated.ModifierType.Name);
        Assert.AreEqual("Int32", annotated.ElementType.Name);
    }

    private static void AssertGenericParameter(TypeReference type)
    {
        var parameter = type as GenericParameter
            ?? throw new AssertFailedException($"expected a generic parameter, found {type.GetType().Name}");
        Assert.AreEqual(0, parameter.Position);
    }

    private static void AssertRuns(byte[] image)
    {
        var context = new AssemblyLoadContext("nested-generic-modifier", isCollectible: true);
        try
        {
            var assembly = context.LoadFromStream(new MemoryStream(image));
            Assert.AreEqual(7, assembly.GetType("IlRepl.Cell")!.GetMethod("Read")!.Invoke(null, null));
        }
        finally
        {
            context.Unload();
        }
    }
}
