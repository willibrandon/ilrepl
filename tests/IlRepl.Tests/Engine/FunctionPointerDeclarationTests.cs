using System.Reflection;
using System.Runtime.Loader;
using IlRepl.Engine;
using Mono.Cecil;
using Mono.Cecil.Cil;
using IsVolatile = System.Runtime.CompilerServices.IsVolatile;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Function-pointer declarations keep their complete signature through binding, emission, and export.
/// </summary>
[TestClass]
public sealed class FunctionPointerDeclarationTests
{
    private static readonly string[] Family =
    [
        ".class public Pointers {",
        ".field public static method int32 *(int32) Current",
        ".method public static int32 Id(int32 value) { ldarg value; ret }",
        ".method public static method int32 *(int32) Pointer() { ldftn int32 Pointers::Id(int32); ret }",
        ".method public static int32 Apply(method int32 *(int32) pointer, int32 value) { "
            + "ldarg value; ldarg pointer; calli int32(int32); ret }",
        ".method public static int32 Call() { call method int32 *(int32) Pointers::Pointer(); ldc.i4.s 42; "
            + "call int32 Pointers::Apply(method int32 *(int32), int32); ret }",
        ".method public static int32 FieldCall() { ldftn int32 Pointers::Id(int32); "
            + "stsfld method int32 *(int32) Pointers::Current; ldsfld method int32 *(int32) Pointers::Current; "
            + "ldc.i4.s 43; call int32 Pointers::Apply(method int32 *(int32), int32); ret }",
        "}",
    ];

    /// <summary>
    /// Members bind to exact function-pointer returns, parameters, and fields before their type closes.
    /// </summary>
    [TestMethod]
    public void ClassMembers_FunctionPointerSignatures_BindAndRenderExactly()
    {
        var session = IlLines.Load([.. Family, "call int32 Pointers::Call()"]);
        Assert.AreEqual(42, session.Run().Value);
        session.AddLine("call int32 Pointers::FieldCall()");
        Assert.AreEqual(43, session.Run().Value);

        var il = session.ToIlAsm();
        Assert.Contains(".field public static method int32 *(int32) Current", il);
        Assert.Contains(".method public static method int32 *(int32) Pointer()", il);
        Assert.Contains("int32 Apply(method int32 *(int32) pointer, int32 'value')", il);
    }

    /// <summary>
    /// A session method and local preserve their function-pointer signatures while their stack type stays native int.
    /// </summary>
    [TestMethod]
    public void SessionMethodAndLocal_FunctionPointerSignatures_RunAndRenderExactly()
    {
        var session = IlLines.Load(
            ".method int32 Id(int32 value) { ldarg value; ret }",
            ".method method int32 *(int32) Pointer() { ldftn int32 Id(int32); ret }",
            ".locals init (method int32 *(int32) pointer)",
            "call method int32 *(int32) Pointer()",
            "stloc pointer",
            "ldc.i4.s 42",
            "ldloc pointer",
            "calli int32(int32)");

        Assert.AreEqual(42, session.Run().Value);
        var pointer = session.Methods.Single(method => method.Signature.Name == "Pointer");
        Assert.IsTrue(pointer.Trampoline.Method.ReturnType.IsFunctionPointer);
        Assert.IsTrue(pointer.Version.Body.ReturnType.IsFunctionPointer);
    }

    /// <summary>
    /// A session method keeps modifiers around exact function-pointer types in its entry point, delegate, and compiled body.
    /// </summary>
    [TestMethod]
    public void SessionMethod_FunctionPointerModifiers_ReachEveryLiveSignature()
    {
        const string marker = "[System.Runtime]System.Runtime.CompilerServices.IsVolatile";
        var session = IlLines.Load(
            $".method method int32 *(int32) modopt({marker}) Echo("
                + $"method int32 *(int32) modreq({marker}) value) {{ ldarg value; ret }}");

        var declaration = session.Methods.Single();
        Assert.AreSequenceEqual([typeof(IsVolatile)], declaration.Signature.ReturnOptionalModifiers);
        Assert.AreSequenceEqual([typeof(IsVolatile)], declaration.Signature.Parameters.Single().RequiredModifiers);
        var method = declaration.Trampoline;
        AssertFunctionPointerModifiers(method.Method);
        AssertFunctionPointerModifiers(method.DelegateType.GetMethod("Invoke")!);
        AssertFunctionPointerModifiers(declaration.Version.Body);

        var context = new AssemblyLoadContext("function-pointer-modifiers", isCollectible: true);
        try
        {
            var image = AssemblyExporter.Write(session, "function-pointer-modifiers");
            var assembly = context.LoadFromStream(new MemoryStream(image));
            AssertFunctionPointerModifiers(assembly.GetType("IlRepl.Cell")!.GetMethod("Echo")!);
        }
        finally
        {
            context.Unload();
        }
    }

    /// <summary>
    /// A function pointer used only as an instruction operand keeps its exact runtime and exported array element type.
    /// </summary>
    [TestMethod]
    public void TypeOperand_FunctionPointerSignature_RunsRendersAndExportsExactly()
    {
        var session = IlLines.Load("ldc.i4.1", "newarr method int32 *(int32)");

        Assert.Contains("newarr method int32 *(int32)", session.ToIlAsm());
        var image = AssemblyExporter.Write(session, "function-pointer-operand");
        var value = (Array)session.Run().Value!;
        Assert.IsTrue(value.GetType().GetElementType()!.IsFunctionPointer);
        using (var definition = AssemblyDefinition.ReadAssembly(new MemoryStream(image)))
        {
            var run = definition.MainModule.GetType("IlRepl.Cell").Methods.Single(method => method.Name == "Run");
            var operand = (TypeReference)run.Body.Instructions.Single(instruction => instruction.OpCode == OpCodes.Newarr).Operand;
            Assert.IsInstanceOfType<FunctionPointerType>(operand);
        }

        var context = new AssemblyLoadContext("function-pointer-operand", isCollectible: true);
        try
        {
            var assembly = context.LoadFromStream(new MemoryStream(image));
            var exported = (Array)assembly.GetType("IlRepl.Cell")!.GetMethod("Run")!.Invoke(null, null)!;
            Assert.IsTrue(exported.GetType().GetElementType()!.IsFunctionPointer);
        }
        finally
        {
            context.Unload();
        }
    }

    /// <summary>
    /// An instruction-only function pointer maps the cell's generic parameter into live and exported metadata.
    /// </summary>
    [TestMethod]
    public void TypeOperand_FunctionPointerWithCellParameter_MapsExactly()
    {
        var session = IlLines.Load(
            ".typeparams (T)",
            ".typeargs (int32)",
            "ldc.i4.1",
            "newarr method !!T *(!!T)");

        var image = AssemblyExporter.Write(session, "generic-function-pointer-operand");
        var value = (Array)session.Run().Value!;
        AssertFunctionPointerElement(value, typeof(int));

        var context = new AssemblyLoadContext("generic-function-pointer-operand", isCollectible: true);
        try
        {
            var assembly = context.LoadFromStream(new MemoryStream(image));
            var run = assembly.GetType("IlRepl.Cell")!.GetMethod("Run")!.MakeGenericMethod(typeof(int));
            AssertFunctionPointerElement((Array)run.Invoke(null, null)!, typeof(int));
        }
        finally
        {
            context.Unload();
        }
    }

    /// <summary>
    /// A call-site return used nowhere else keeps its function-pointer signature in listings and exported metadata.
    /// </summary>
    [TestMethod]
    public void CallSite_FunctionPointerSignature_RunsRendersAndExportsExactly()
    {
        var session = IlLines.Load(
            ".method int32 Id(int32 value) { ldarg value; ret }",
            ".method method int32 *(int32) Pointer() { ldftn int32 Id(int32); ret }",
            "ldc.i4.s 42",
            "ldftn method int32 *(int32) Pointer()",
            "calli method int32 *(int32)()",
            "calli int32(int32)");

        var il = session.ToIlAsm();
        Assert.Contains("calli method int32 *(int32)()", il);
        var image = AssemblyExporter.Write(session, "function-pointer-call-site");
        Assert.AreEqual(42, session.Run().Value);
        using var definition = AssemblyDefinition.ReadAssembly(new MemoryStream(image));
        var run = definition.MainModule.GetType("IlRepl.Cell").Methods.Single(method => method.Name == "Run");
        var sites = run.Body.Instructions.Where(instruction => instruction.OpCode == OpCodes.Calli)
            .Select(instruction => (CallSite)instruction.Operand).ToArray();
        Assert.IsInstanceOfType<FunctionPointerType>(sites[0].ReturnType);

        var context = new AssemblyLoadContext("function-pointer-call-site", isCollectible: true);
        try
        {
            var assembly = context.LoadFromStream(new MemoryStream(IlasmLocator.Assemble(il)));
            Assert.AreEqual(42, assembly.GetType("IlRepl.Cell")!.GetMethod("Run")!.Invoke(null, null));
        }
        finally
        {
            context.Unload();
        }
    }

    /// <summary>
    /// Exported fields and methods retain function-pointer metadata and execute through those signatures.
    /// </summary>
    [TestMethod]
    public void Export_FunctionPointerDeclarations_RetainMetadataAndRun()
    {
        var session = IlLines.Load(Family);
        var image = AssemblyExporter.Write(session, "function-pointers");
        var context = new AssemblyLoadContext("function-pointers", isCollectible: true);
        try
        {
            var assembly = context.LoadFromStream(new MemoryStream(image));
            var pointers = assembly.GetType("Pointers")!;
            Assert.IsTrue(pointers.GetField("Current")!.FieldType.IsFunctionPointer);
            Assert.IsTrue(pointers.GetMethod("Pointer")!.ReturnType.IsFunctionPointer);
            Assert.IsTrue(pointers.GetMethod("Apply")!.GetParameters()[0].ParameterType.IsFunctionPointer);
            Assert.AreEqual(42, pointers.GetMethod("Call")!.Invoke(null, null));
            Assert.AreEqual(43, pointers.GetMethod("FieldCall")!.Invoke(null, null));
        }
        finally
        {
            context.Unload();
        }
    }

    /// <summary>
    /// Replacing a type rebuilds declarations that name it only inside a function-pointer signature.
    /// </summary>
    [TestMethod]
    public void Replacement_FunctionPointerSignature_RebindsItsNestedTypes()
    {
        var session = IlLines.Load(
            ".class public Point { }",
            ".class public Host {",
            ".field public static method class Point *(class Point) Transform",
            "}");

        var message = "";
        foreach (var line in IlLines.Expand(".class public Point {", ".field public int32 X", "}"))
        {
            message = session.AddLine(line).Message ?? message;
        }

        Assert.Contains("rebuilt class Host", message);
        var point = session.Types.Single(type => type.Declaration.Name == "Point").RuntimeType!;
        var pointer = session.Types.Single(type => type.Declaration.Name == "Host").RuntimeType!
            .GetField("Transform")!.FieldType;
        Assert.AreSame(point, pointer.GetFunctionPointerReturnType());
        Assert.AreSame(point, pointer.GetFunctionPointerParameterTypes().Single());
        _ = AssemblyExporter.Write(session, "function-pointer-replacement");
    }

    /// <summary>
    /// Replacing a type rebuilds a method that names it only inside a function-pointer instruction operand.
    /// </summary>
    [TestMethod]
    public void Replacement_FunctionPointerOperand_RebindsItsNestedTypes()
    {
        var session = IlLines.Load(
            ".class public Point { }",
            ".method object Make() { ldc.i4.1; newarr method class Point *(class Point); ret }",
            ".method object Indirect() { ldnull; ldc.i4.0; conv.i; calli object(class Point); ret }");

        var message = "";
        foreach (var line in IlLines.Expand(".class public Point {", ".field public int32 X", "}"))
        {
            message = session.AddLine(line).Message ?? message;
        }

        Assert.Contains("rebuilt method Make", message);
        Assert.Contains("method Indirect", message);
        var point = session.Types.Single(type => type.Declaration.Name == "Point").RuntimeType!;
        var array = (Array)session.Methods.Single(method => method.Signature.Name == "Make").Version.Body.Invoke(null, null)!;
        var pointer = array.GetType().GetElementType()!;
        Assert.IsTrue(pointer.IsFunctionPointer);
        Assert.AreSame(point, pointer.GetFunctionPointerReturnType());
        Assert.AreSame(point, pointer.GetFunctionPointerParameterTypes().Single());
        _ = AssemblyExporter.Write(session, "function-pointer-operand-replacement");
    }

    /// <summary>
    /// Type and method parameters retain their owners inside function-pointer signatures.
    /// </summary>
    [TestMethod]
    public void GenericParameters_FunctionPointerSignatures_SubstituteByOwner()
    {
        var session = IlLines.Load(
            ".class public Box<T> {",
            ".method public static !!0 Id<U>(!!0 value) { ldarg value; ret }",
            ".method public static method !!0 *(!!0) Pointer<U>() { ldftn !!0 Box<!0>::Id<!!0>(!!0); ret }",
            ".method public static !0 Call(!0 value) { ldarg value; call method !0 *(!0) Box<!0>::Pointer<!0>(); "
                + "calli !0(!0); ret }",
            "}",
            "ldc.i4.s 42",
            "call int32 Box<int32>::Call(int32)");

        Assert.AreEqual(42, session.Run().Value);
        var il = session.ToIlAsm();
        Assert.Contains(".method public static method !!U *(!!U) Pointer<U>()", il);
        foreach (var image in new[] { AssemblyExporter.Write(session, "generic-function-pointer"),
            IlasmLocator.Assemble(il) })
        {
            var context = new AssemblyLoadContext("generic-function-pointer", isCollectible: true);
            try
            {
                var assembly = context.LoadFromStream(new MemoryStream(image));
                var box = assembly.GetType("Box`1")!.MakeGenericType(typeof(int));
                Assert.AreEqual(42, box.GetMethod("Call")!.Invoke(null, [42]));
            }
            finally
            {
                context.Unload();
            }
        }
    }

    /// <summary>
    /// A modified generic method argument keeps its modifier in listings, saved metadata, and execution.
    /// </summary>
    [TestMethod]
    public void GenericMethodArgument_ModifiedType_RunsRendersAndExportsExactly()
    {
        var modifier = "[System.Runtime]System.Runtime.CompilerServices.IsLong";
        var session = IlLines.Load(
            ".class public GenericCalls {",
            ".method public static void Ignore<T>() { ret }",
            "}",
            $"call void GenericCalls::Ignore<int32 modopt({modifier})>()",
            "ldc.i4.s 42");

        var il = session.ToIlAsm();
        Assert.Contains("Ignore<int32 modopt(", il);
        Assert.Contains("System.Runtime.CompilerServices.IsLong", il);
        AssertGenericArgument<OptionalModifierType>(AssemblyExporter.Write(session, "modified-generic-argument"));
        AssertGenericArgument<OptionalModifierType>(IlasmLocator.Assemble(il));
        Assert.AreEqual(42, session.Run().Value);
    }

    /// <summary>
    /// A function-pointer generic method argument stays a function pointer even when the runtime rejects the MethodSpec.
    /// </summary>
    [TestMethod]
    public void GenericMethodArgument_FunctionPointer_RendersAndExportsExactly()
    {
        var session = IlLines.Load(
            ".class public GenericCalls {",
            ".method public static void Ignore<T>() { ret }",
            "}",
            "call void GenericCalls::Ignore<method int32 *(int32)>()",
            "ldc.i4.s 42");

        var il = session.ToIlAsm();
        Assert.Contains("Ignore<method int32 *(int32)>()", il);
        AssertGenericArgument<FunctionPointerType>(AssemblyExporter.Write(session, "function-pointer-generic-argument"));
        AssertGenericArgument<FunctionPointerType>(IlasmLocator.Assemble(il));
        var exception = Assert.ThrowsExactly<CellException>(() => session.Run());
        Assert.IsInstanceOfType<BadImageFormatException>(exception.InnerException);
    }

    private static void AssertGenericArgument<T>(byte[] image)
        where T : TypeReference
    {
        using var definition = AssemblyDefinition.ReadAssembly(new MemoryStream(image));
        var run = definition.MainModule.GetType("IlRepl.Cell").Methods.Single(method => method.Name == "Run");
        var call = Assert.IsInstanceOfType<GenericInstanceMethod>(
            run.Body.Instructions.Single(instruction => instruction.OpCode == OpCodes.Call).Operand);
        Assert.IsInstanceOfType<T>(call.GenericArguments.Single());
    }

    private static void AssertFunctionPointerElement(Array array, Type expected)
    {
        var pointer = array.GetType().GetElementType()!;
        Assert.IsTrue(pointer.IsFunctionPointer);
        Assert.AreSame(expected, pointer.GetFunctionPointerReturnType());
        Assert.AreSame(expected, pointer.GetFunctionPointerParameterTypes().Single());
    }

    private static void AssertFunctionPointerModifiers(MethodInfo method)
    {
        Assert.IsTrue(method.ReturnType.IsFunctionPointer);
        Assert.AreSequenceEqual(
            [typeof(IsVolatile)],
            method.ReturnParameter.GetOptionalCustomModifiers());
        Assert.IsTrue(method.GetParameters().Single().ParameterType.IsFunctionPointer);
        Assert.AreSequenceEqual(
            [typeof(IsVolatile)],
            method.GetParameters().Single().GetRequiredCustomModifiers());
    }
}
