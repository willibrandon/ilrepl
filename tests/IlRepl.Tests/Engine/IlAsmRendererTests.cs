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
}
