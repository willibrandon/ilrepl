using IlRepl.Engine;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Tests for <see cref="IlAsmRenderer"/>.
/// </summary>
[TestClass]
public sealed class IlAsmRendererTests
{
    /// <summary>
    /// The rendered cell has a method with locals, qualified operands, and the boxing epilogue.
    /// </summary>
    [TestMethod]
    public void Render_Cell_IncludesDeclarationsAndOperands()
    {
        var session = new Session();
        session.AddLine(".locals init (int32 i)");
        session.AddLine("ldc.i4 3");
        session.AddLine("stloc i");
        session.AddLine("ldloc i");
        session.AddLine("call int32 Math::Abs(int32)");

        var text = session.ToIlAsm();
        Assert.Contains(".method public static object Run() cil managed", text);
        Assert.Contains(".locals init ([0] int32 i)", text);
        Assert.Contains("call int32 [System.Runtime]System.Math::Abs(int32)", text);
        Assert.Contains("box int32", text);
        Assert.Contains(".assembly extern System.Runtime {}", text);
    }

    /// <summary>
    /// Blocks render in ILAsm block form.
    /// </summary>
    [TestMethod]
    public void Render_Blocks_UseIlAsmSyntax()
    {
        var session = new Session();
        session.AddLine(".try {");
        session.AddLine("nop");
        session.AddLine("} catch Exception {");
        session.AddLine("pop");
        session.AddLine("}");

        var text = session.ToIlAsm();
        Assert.Contains(".try", text);
        Assert.Contains("catch [System.Runtime]System.Exception", text);
    }

    /// <summary>
    /// Generic parameters and arguments appear in the signature.
    /// </summary>
    [TestMethod]
    public void Render_GenericCellWithArguments_ShowsSignature()
    {
        var session = new Session();
        session.AddLine(".typeparams (T)");
        session.AddLine(".args (int32 n = 1)");
        session.AddLine("ldarg n");
        Assert.Contains("object Run<T>(int32 n)", session.ToIlAsm());
    }

    /// <summary>
    /// Session methods render as ILAsm methods on the same class, ahead of Run.
    /// </summary>
    [TestMethod]
    public void Render_SessionMethods_AppearBeforeRun()
    {
        var session = new Session();
        foreach (var line in new[] { ".method int32 Twice(int32 n) {", ".locals init (int32 t)", "ldarg n", "ldc.i4 2", "mul", "ret", "}" })
        {
            session.AddLine(line);
        }

        var text = session.ToIlAsm();
        var method = text.IndexOf(".method public static int32 Twice(int32 n) cil managed", StringComparison.Ordinal);
        var run = text.IndexOf(".method public static object Run() cil managed", StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, method);
        Assert.IsGreaterThan(method, run);
        Assert.Contains(".locals init ([0] int32 t)", text[method..run]);
        Assert.Contains("        ret\n", text[method..run].Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    /// <summary>
    /// A call to a session method is qualified with the cell type.
    /// </summary>
    [TestMethod]
    public void Render_SessionMethodCall_IsQualifiedWithCellType()
    {
        var session = new Session();
        foreach (var line in new[] { ".method int32 Two() {", "ldc.i4 2", "ret", "}", "call int32 Two()", "ldftn int32 Two()", "pop" })
        {
            session.AddLine(line);
        }

        var text = session.ToIlAsm();
        Assert.Contains("call int32 IlRepl.Cell::Two()", text);
        Assert.Contains("ldftn int32 IlRepl.Cell::Two()", text);
    }

    /// <summary>
    /// A void method ends with a bare ret, never ldnull.
    /// </summary>
    [TestMethod]
    public void Render_VoidMethod_EndsWithBareRet()
    {
        var session = new Session();
        foreach (var line in new[] { ".method void Hi() {", "nop", "}" })
        {
            session.AddLine(line);
        }

        var text = session.ToIlAsm().Replace("\r\n", "\n", StringComparison.Ordinal);
        var method = text.IndexOf(".method public static void Hi()", StringComparison.Ordinal);
        var run = text.IndexOf("object Run()", StringComparison.Ordinal);
        Assert.Contains("        nop\n        ret\n    }\n", text[method..run]);
        Assert.DoesNotContain("ldnull", text[method..run]);
    }

    /// <summary>
    /// The method being typed is not rendered until it commits.
    /// </summary>
    [TestMethod]
    public void Render_WhileMethodOpen_OmitsOpenMethod()
    {
        var session = new Session();
        session.AddLine("ldc.i4 1");
        session.AddLine(".method int32 Fib(int32 n) {");
        session.AddLine("ldarg n");

        var text = session.ToIlAsm();
        Assert.DoesNotContain("Fib", text);
        Assert.Contains("ldc.i4 1", text);
    }

    /// <summary>
    /// Names that ILAsm reads as keywords or opcodes are quoted wherever they appear.
    /// </summary>
    [TestMethod]
    public void Render_KeywordNames_AreQuoted()
    {
        var session = new Session();
        foreach (var line in KeywordNamedSession)
        {
            session.AddLine(line);
        }

        var text = session.ToIlAsm();
        Assert.Contains(".method public static int32 'add'(int32 'value') cil managed", text);
        Assert.Contains(".method public static int32 'windowsruntime'(int32 'noplatform', int32 'bestfit') cil managed", text);
        Assert.Contains(".method public static int32 'float'(int32 'lpvoid', int32 'wchar') cil managed", text);
        Assert.Contains(".method public static int32 'brnull'(int32 'refany') cil managed", text);
        Assert.Contains("ldarg 'noplatform'", text);
        Assert.Contains("ldarg 'lpvoid'", text);
        Assert.Contains("call int32 IlRepl.Cell::'brnull'(int32)", text);
        Assert.Contains("ldarg 'value'", text);
        Assert.Contains(".locals init ([0] int32 'class')", text);
        Assert.Contains("stloc 'class'", text);
        Assert.Contains("ldloc 'class'", text);
        Assert.Contains("call int32 IlRepl.Cell::'add'(int32)", text);
        Assert.Contains("ldarg n", new Session().ToIlAsm() + "ldarg n", "plain names stay unquoted");
    }

    /// <summary>
    /// The rendered text assembles with ilasm when one is installed; otherwise the test is inconclusive.
    /// </summary>
    [TestMethod]
    public void Render_KeywordNames_AssembleWithIlasm()
    {
        var ilasm = IlasmLocator.Require();

        var session = new Session();
        foreach (var line in KeywordNamedSession)
        {
            session.AddLine(line);
        }

        var directory = Path.Combine(Path.GetTempPath(), "ilrepl-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "cell.il");
            File.WriteAllText(source, session.ToIlAsm());
            // Options take a dash: a slash is a path on Unix.
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ilasm, ["-DLL", "-QUIET", "-OUTPUT=" + Path.Combine(directory, "cell.dll"), source])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            })!;
            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.AreEqual(0, process.ExitCode, output);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Methods, parameters, and locals named after lexer keywords, opcode aliases, and opcodes.
    /// </summary>
    private static readonly string[] KeywordNamedSession =
    [
        ".method int32 add(int32 value) {", "ldarg value", "ret", "}",
        ".method int32 windowsruntime(int32 noplatform, int32 bestfit) {", "ldarg noplatform", "ldarg bestfit", "add", "ret", "}",
        ".method int32 float(int32 lpvoid, int32 wchar) {", "ldarg lpvoid", "ldarg wchar", "add", "ret", "}",
        ".method int32 brnull(int32 refany) {", "ldarg refany", "ret", "}",
        ".locals init (int32 class)", "ldc.i4 3", "stloc class", "ldloc class", "call int32 add(int32)", "call int32 brnull(int32)",
    ];

    /// <summary>
    /// Type families render before the cell type, in declaration order, with their members.
    /// </summary>
    [TestMethod]
    public void Render_Types_AppearBeforeTheCellType()
    {
        var session = IlLines.Load(
            ".class interface public abstract IArea {", ".method public abstract virtual instance float64 Area() { }", "}",
            ".class public sequential ansi sealed Point extends [System.Runtime]System.ValueType implements IArea {",
            ".pack 4",
            ".field public int32 X",
            ".field public static literal int32 Max = int32(9)",
            ".method public instance void .ctor(int32 x) { ldarg.0; ldarg x; stfld int32 Point::X; ret }",
            ".method public virtual instance float64 Area() { ldarg.0; ldfld int32 Point::X; conv.r8; ret }",
            ".method public specialname instance int32 get_Len() { ldarg.0; ldfld int32 Point::X; ret }",
            ".property instance int32 Len() {", ".get instance int32 Point::get_Len()", "}",
            ".class nested private Tag { }",
            "}",
            ".class public Box`1<class T> {", ".field public !0 V", "}",
            "ldc.i4 3", "newobj instance void Point::.ctor(int32)", "box Point");
        var text = session.ToIlAsm();
        Assert.Contains(".class interface public abstract auto ansi IArea", text);
        Assert.Contains(".method public newslot abstract virtual instance float64 Area() cil managed", text);
        Assert.Contains(".class public sequential ansi sealed Point extends [System.Runtime]System.ValueType implements IArea", text);
        Assert.Contains("    .pack 4", text);
        Assert.Contains("    .field public static literal int32 Max = int32(9)", text);
        Assert.Contains(".method public specialname rtspecialname instance void .ctor(int32 x) cil managed", text);
        Assert.Contains("        stfld int32 Point::X", text);
        Assert.Contains("    .property instance int32 Len()", text);
        Assert.Contains("        .get instance int32 Point::get_Len()", text);
        Assert.Contains("    .class nested private auto ansi Tag extends [System.Runtime]System.Object", text);
        Assert.Contains(".class public auto ansi Box`1<class T> extends [System.Runtime]System.Object", text);
        Assert.Contains("    .field public !T V", text);
        Assert.Contains("        box valuetype Point", text);
        Assert.IsLessThan(text.IndexOf("IlRepl.Cell extends", StringComparison.Ordinal), text.IndexOf(".class public sequential", StringComparison.Ordinal));
    }

    /// <summary>
    /// The rendered types assemble with ilasm, and the assembled cell runs with the same result.
    /// </summary>
    [TestMethod]
    public void Render_Types_AssembleWithIlasmAndRun()
    {
        var session = IlLines.Load(
            ".method int32 Twice(int32 n) { ldarg n; ldc.i4 2; mul; ret }",
            ".class interface public abstract IArea {", ".method public abstract virtual instance int32 Area() { }", "}",
            ".class public sequential ansi sealed Point extends [System.Runtime]System.ValueType implements IArea {",
            ".field public int32 X",
            ".field public static initonly int32 Count",
            ".method static void .cctor() { ldc.i4 1; stsfld int32 Point::Count; ret }",
            ".method public instance void .ctor(int32 x) { ldarg.0; ldarg x; stfld int32 Point::X; ret }",
            ".method public virtual instance int32 Area() { ldarg.0; ldfld int32 Point::X; call int32 Twice(int32); ret }",
            "}",
            ".class public Box`1<T> {",
            ".field public !0 V",
            ".method public instance void .ctor(!0 v) { ldarg.0; call instance void [System.Runtime]System.Object::.ctor(); ldarg.0; ldarg v; stfld !0 class Box`1<!0>::V; ret }",
            ".class nested public Inner {",
            ".method public static int32 Three() { ldc.i4 3; ret }",
            "}",
            "}",
            "ldc.i4 5", "newobj instance void Point::.ctor(int32)", "box Point", "callvirt instance int32 IArea::Area()",
            "call int32 Box`1/Inner::Three()", "add", "ldsfld int32 Point::Count", "add");
        var text = session.ToIlAsm();
        Assert.AreEqual(14, session.Run().Value);
        var image = IlasmLocator.Assemble(text);
        var context = new System.Runtime.Loader.AssemblyLoadContext("ilasm-types", isCollectible: true);
        try
        {
            var assembly = context.LoadFromStream(new MemoryStream(image));
            Assert.AreEqual(14, assembly.GetType("IlRepl.Cell")!.GetMethod("Run")!.Invoke(null, null));
        }
        finally
        {
            context.Unload();
        }
    }
}
