using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using IlRepl.Engine;

namespace IlRepl.Tests.Engine;

/// <summary>
/// The exported assembly carries the session's types and methods with the metadata that was
/// declared, references no session assembly, and runs when loaded.
/// </summary>
[TestClass]
public sealed class AssemblyExporterTests
{
    private static readonly string[] Geometry =
    [
        ".class interface public abstract IArea {",
        ".method public abstract virtual instance float64 Area() { }",
        "}",
        ".class public sequential ansi sealed Point extends [System.Runtime]System.ValueType implements IArea {",
        ".pack 4",
        ".field public int32 X",
        ".field public int32 Y",
        ".method public instance void .ctor(int32 x, int32 y) { ldarg.0; ldarg x; stfld int32 Point::X; ldarg.0; ldarg y; stfld int32 Point::Y; ret }",
        ".method public virtual instance float64 Area() { ldarg.0; ldfld int32 Point::X; ldarg.0; ldfld int32 Point::Y; mul; conv.r8; ret }",
        "}",
        ".class public Line {",
        ".field public valuetype Point A",
        ".field public static int32 Made",
        ".method public instance void .ctor(valuetype Point a) { ldarg.0; call instance void [System.Runtime]System.Object::.ctor(); ldarg.0; ldarg a; stfld valuetype Point Line::A; ldsfld int32 Line::Made; ldc.i4 1; add; stsfld int32 Line::Made; ret }",
        ".method public instance int32 Sum() { ldarg.0; ldflda valuetype Point Line::A; ldfld int32 Point::X; ldarg.0; ldflda valuetype Point Line::A; ldfld int32 Point::Y; add; call int32 Twice(int32); ret }",
        "}",
    ];

    private static Session Load(params string[] lines) => IlLines.Load(lines);

    private static (Assembly Assembly, AssemblyLoadContext Context) LoadExport(Session session, string name)
    {
        var image = AssemblyExporter.Write(session, name);
        var context = new AssemblyLoadContext("export-" + name, isCollectible: true);
        return (context.LoadFromStream(new MemoryStream(image)), context);
    }

    /// <summary>
    /// Types, session methods, and the cell run from the saved assembly, with the same results.
    /// </summary>
    [TestMethod]
    public void Write_TypesMethodsAndCell_Run()
    {
        var session = Load([".method int32 Twice(int32 n) { ldarg n; ldc.i4 2; mul; ret }", .. Geometry,
            "ldc.i4 3", "ldc.i4 4", "newobj instance void Point::.ctor(int32, int32)", "newobj instance void Line::.ctor(valuetype Point)", "call instance int32 Line::Sum()"]);
        Assert.AreEqual(14, session.Run().Value);
        foreach (var line in new[] { "ldc.i4 3", "ldc.i4 4", "newobj instance void Point::.ctor(int32, int32)", "newobj instance void Line::.ctor(valuetype Point)", "call instance int32 Line::Sum()" })
        {
            session.AddLine(line);
        }

        var (assembly, context) = LoadExport(session, "geometry");
        try
        {
            var cell = assembly.GetType("IlRepl.Cell")!;
            Assert.AreEqual(14, cell.GetMethod("Run")!.Invoke(null, null));
            Assert.AreEqual(10, cell.GetMethod("Twice")!.Invoke(null, [5]));
            var point = assembly.GetType("Point")!;
            Assert.IsTrue(point.IsValueType);
            Assert.AreEqual(4, point.StructLayoutAttribute!.Pack);
            Assert.AreEqual(typeof(double), point.GetMethod("Area")!.ReturnType);
            var line = assembly.GetType("Line")!;
            Assert.AreEqual(point, line.GetField("A")!.FieldType);
            Assert.AreEqual(1, line.GetField("Made")!.GetValue(null), "the export's own static, counted by its own Run");
            Assert.IsEmpty(assembly.GetReferencedAssemblies().Where(a => a.Name!.StartsWith("ilrepl", StringComparison.Ordinal)), "nothing in the export names a session assembly");
        }
        finally
        {
            context.Unload();
        }
    }

    /// <summary>
    /// The saved metadata is what was declared: layouts and offsets, no synthesized constructor,
    /// non-vector arrays, a parameter default that is not optional, modifiers, and attributes.
    /// </summary>
    [TestMethod]
    public void Write_KeepsDeclaredMetadata()
    {
        var session = Load(
            ".class public explicit sealed Union extends [System.Runtime]System.ValueType {",
            ".size 8",
            ".field [0] public int32 I",
            ".field [0] public float32 F",
            "}",
            ".class public Bare {",
            ".custom instance void [System.Runtime]System.ObsoleteAttribute::.ctor(string) = { string('bare') }",
            ".field public static int32[0...] Rows",
            ".field public static int32 modreq([System.Runtime]System.Runtime.CompilerServices.IsVolatile) V",
            ".method public static int32 Take(int32[0...] a, int32 n) { .param [2] = int32(7); ldarg n; ret }",
            "}");
        var image = AssemblyExporter.Write(session, "declared");
        using var pe = new PEReader(new MemoryStream(image));
        var reader = pe.GetMetadataReader();
        Assert.AreEqual(2, reader.GetTableRowCount(TableIndex.FieldLayout));
        Assert.AreEqual(1, reader.GetTableRowCount(TableIndex.ClassLayout), "the struct with .size has a layout row; the class has none");
        Assert.IsEmpty(reader.AssemblyReferences.Select(h => reader.GetString(reader.GetAssemblyReference(h).Name)).Where(n => n.StartsWith("ilrepl", StringComparison.Ordinal)));
        var context = new AssemblyLoadContext("declared", isCollectible: true);
        try
        {
            var assembly = context.LoadFromStream(new MemoryStream(image));
            var bare = assembly.GetType("Bare")!;
            Assert.IsEmpty(bare.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));
            Assert.IsFalse(bare.GetField("Rows")!.FieldType.IsSZArray);
            Assert.AreEqual("IsVolatile", bare.GetField("V")!.GetRequiredCustomModifiers()[0].Name);
            var take = bare.GetMethod("Take")!.GetParameters();
            Assert.IsFalse(take[0].ParameterType.IsSZArray);
            Assert.AreEqual(7, take[1].DefaultValue);
            Assert.IsFalse(take[1].IsOptional);
            Assert.AreEqual("bare", bare.GetCustomAttribute<ObsoleteAttribute>()!.Message);
            var union = assembly.GetType("Union")!;
            Assert.AreEqual(System.Runtime.InteropServices.LayoutKind.Explicit, union.StructLayoutAttribute!.Value);
            Assert.AreEqual(8, union.StructLayoutAttribute.Size);
        }
        finally
        {
            context.Unload();
        }
    }

    /// <summary>
    /// Generic and nested types, and members reached through instantiations, export and run.
    /// </summary>
    [TestMethod]
    public void Write_GenericAndNestedTypes_Run()
    {
        var session = Load(
            ".class public Box`1<T> {",
            ".field public !0 V",
            ".field public static int32 Made",
            ".class nested public Tag { .field public static int32 N }",
            ".method public instance void .ctor(!0 v) { ldarg.0; call instance void [System.Runtime]System.Object::.ctor(); ldarg.0; ldarg v; stfld !0 class Box`1<!0>::V; ldsfld int32 class Box`1<!0>::Made; ldc.i4 1; add; stsfld int32 class Box`1<!0>::Made; ret }",
            ".method public instance !0 Get() { ldarg.0; ldfld !0 class Box`1<!0>::V; ret }",
            "}",
            ".method int32 Unbox(class Box`1<int32> b) { ldarg b; call instance !0 class Box`1<int32>::Get(); ret }",
            "ldc.i4 6", "newobj instance void class Box`1<int32>::.ctor(!0)", "call int32 Unbox(class Box`1<int32>)");
        var (assembly, context) = LoadExport(session, "boxes");
        try
        {
            Assert.AreEqual(6, assembly.GetType("IlRepl.Cell")!.GetMethod("Run")!.Invoke(null, null));
            var box = assembly.GetType("Box`1")!;
            Assert.IsTrue(box.IsGenericTypeDefinition);
            Assert.AreEqual("Tag", box.GetNestedType("Tag")!.Name);
            Assert.AreEqual(1, box.MakeGenericType(typeof(int)).GetField("Made")!.GetValue(null));
        }
        finally
        {
            context.Unload();
        }
    }

    /// <summary>
    /// A type argument in an attribute names the exported type, not the session's.
    /// </summary>
    [TestMethod]
    public void Write_AttributeTypeArgument_NamesTheExportedType()
    {
        var session = Load(
            ".class public Point { }",
            ".class public Line {",
            ".custom instance void [System.Runtime]System.Diagnostics.DebuggerTypeProxyAttribute::.ctor(class [System.Runtime]System.Type) = { type(Point) }",
            "}");
        var (assembly, context) = LoadExport(session, "proxied");
        try
        {
            var line = assembly.GetType("Line")!;
            var argument = line.GetCustomAttributesData()[0].ConstructorArguments[0].Value as Type;
            Assert.IsNotNull(argument);
            Assert.AreSame(assembly.GetType("Point"), argument);
        }
        finally
        {
            context.Unload();
        }
    }

    /// <summary>
    /// An open block or an incomplete cell refuses the export as it refuses a run.
    /// </summary>
    [TestMethod]
    public void Write_Incomplete_IsRefused()
    {
        var open = Load(".class public Open {");
        Assert.Contains("class Open is still open", Assert.ThrowsExactly<ReplException>(() => AssemblyExporter.Write(open, "x")).Message);
        var pending = Load("br NOWHERE");
        Assert.Contains("never defined", Assert.ThrowsExactly<ReplException>(() => AssemblyExporter.Write(pending, "x")).Message);
    }

    /// <summary>
    /// A family listed first may mention one listed later, since every family is declared
    /// before any shape is imported.
    /// </summary>
    [TestMethod]
    public void Write_FamiliesInAnyOrder()
    {
        var session = Load(".class public A { }", ".class public B { }", ".class public A {", ".field public class B Other", "}");
        var (assembly, context) = LoadExport(session, "ordered");
        try
        {
            Assert.AreEqual(assembly.GetType("B"), assembly.GetType("A")!.GetField("Other")!.FieldType);
        }
        finally
        {
            context.Unload();
        }
    }

    /// <summary>
    /// A framework generic instantiated with a session type is written over the exported type.
    /// </summary>
    [TestMethod]
    public void Write_FrameworkGenericOverASessionType()
    {
        var session = Load(".class public Point {", ".method public instance void .ctor() { ldarg.0; call instance void [System.Runtime]System.Object::.ctor(); ret }", "}",
            "newobj instance void class [System.Collections]System.Collections.Generic.List`1<class Point>::.ctor()",
            "dup", "newobj instance void Point::.ctor()", "callvirt instance void class [System.Collections]System.Collections.Generic.List`1<class Point>::Add(!0)",
            "callvirt instance int32 class [System.Collections]System.Collections.Generic.List`1<class Point>::get_Count()");
        var (assembly, context) = LoadExport(session, "listed");
        try
        {
            Assert.AreEqual(1, assembly.GetType("IlRepl.Cell")!.GetMethod("Run")!.Invoke(null, null));
        }
        finally
        {
            context.Unload();
        }
    }

    /// <summary>
    /// An attribute type declared in the session is applied and exported; its constructor is a
    /// definition of the export before any attribute is imported.
    /// </summary>
    [TestMethod]
    public void Write_SessionDefinedAttribute()
    {
        var session = Load(
            ".class public Marker extends [System.Runtime]System.Attribute {", ".method public instance void .ctor() { ldarg.0; call instance void [System.Runtime]System.Attribute::.ctor(); ret }", "}",
            ".class public Tagged {", ".custom instance void Marker::.ctor() = ( 01 00 00 00 )", ".field public int32 X", ".custom instance void Marker::.ctor() = ( 01 00 00 00 )", "}");
        Assert.AreEqual("Marker", session.Types[1].RuntimeType!.GetCustomAttributesData()[0].AttributeType.Name);
        var (assembly, context) = LoadExport(session, "marked");
        try
        {
            var tagged = assembly.GetType("Tagged")!;
            Assert.AreEqual(assembly.GetType("Marker"), tagged.GetCustomAttributesData()[0].AttributeType);
            Assert.AreEqual(assembly.GetType("Marker"), tagged.GetField("X")!.GetCustomAttributesData()[0].AttributeType);
        }
        finally
        {
            context.Unload();
        }
    }
}
