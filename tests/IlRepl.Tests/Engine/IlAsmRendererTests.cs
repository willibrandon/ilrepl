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
}
