using System.Text.Json;
using IlRepl.Engine;
using IlRepl.Repl;
using IlRepl.Tests.Shared;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Reconstructed definitions remain inspectable until an explicit execution activates their runtime state.
/// </summary>
[TestClass]
public sealed class SessionActivationTests
{
    /// <summary>
    /// An incompatible implementation delegate is rejected without replacing the last callable method body.
    /// </summary>
    [TestMethod]
    public void Trampoline_BindRejectsUnrelatedDelegateWithoutChangingImplementation()
    {
        using var core = new ReplCore();
        var session = core.Session;
        Submit(session,
            ".method int32 First() { ldc.i4.s 42; ret }",
            ".method int32 Second() { ldc.i4.s 43; ret }");
        var first = session.Methods.Single(method => method.Signature.Name == "First");
        var second = session.Methods.Single(method => method.Signature.Name == "Second");
        Assert.ThrowsExactly<InvalidCastException>(() => first.Trampoline.Bind(second.Version.Implementation));
        session.AddLine("call First");
        Assert.AreEqual(42, session.Run().Value);
        session.AddLine("call Second");
        Assert.AreEqual(43, session.Run().Value);
    }

    /// <summary>
    /// Retaining method metadata leaves its module initializer untouched until the execution delegate is requested.
    /// </summary>
    [TestMethod]
    public void CompiledVersion_DefersDelegateCreationAndModuleInitialization()
    {
        var marker = Path.Combine(Path.GetTempPath(), "ilrepl-activation-" + Guid.NewGuid().ToString("N"));
        var image = ModuleInitializerFixture.Create(true, marker);
        var definition = SessionAssemblies.Load(image, "activation-" + Guid.NewGuid().ToString("N"), SessionAssemblyKind.Methods, []);
        try
        {
            var body = definition.Assembly.GetType("Owner")!.GetMethod("Read")!;
            var version = new CompiledMethodVersion(definition, body, typeof(Func<int>));
            Assert.AreSame(definition, version.Definition);
            Assert.AreSame(body, version.Body);
            Assert.IsFalse(File.Exists(marker), "Retaining a method version executed its module initializer.");

            var implementation = Assert.IsInstanceOfType<Func<int>>(version.Implementation);
            Assert.AreSame(implementation, version.Implementation);
            Assert.AreEqual(142, implementation());
            Assert.AreEqual("initialized\n", File.ReadAllText(marker));
            Assert.AreEqual(142, implementation());
            Assert.AreEqual("initialized\n", File.ReadAllText(marker));
        }
        finally
        {
            SessionAssemblies.Release(definition);
            File.Delete(marker);
        }
    }

    /// <summary>
    /// Explicit cell execution binds reconstructed ordinary methods and uses their latest committed versions.
    /// </summary>
    [TestMethod]
    public void Run_ActivatesDeferredMethodChainAndLatestRedefinition()
    {
        var session = new Session { DeferActivation = true };
        Submit(session,
            ".method int32 Answer() { ldc.i4.s 21; ret }",
            ".method int32 Twice() { call Answer; ldc.i4.2; mul; ret }",
            ".method int32 Answer() { ldc.i4.s 42; ret }",
            "call Twice");

        Assert.IsTrue(session.DeferActivation);
        Assert.HasCount(2, session.Methods);
        Assert.Contains("Twice", session.ToIlAsm());
        Assert.IsTrue(session.DeferActivation);
        Assert.AreEqual(84, session.Run().Value);
        Assert.IsFalse(session.DeferActivation);

        Submit(session, ".method int32 Answer() { ldc.i4.s 43; ret }", "call Twice");
        Assert.AreEqual(86, session.Run().Value);
    }

    /// <summary>
    /// Rebuilding a type and its dependent method leaves the deferred trampoline ready for later execution.
    /// </summary>
    [TestMethod]
    public void Run_ActivatesMethodsRebuiltForADeferredTypeReplacement()
    {
        var session = new Session { DeferActivation = true };
        Submit(session,
            ".class public ActivationOwner {",
            ".method public static int32 Read() { ldc.i4.s 21; ret }",
            "}",
            ".method int32 ReadOwner() { call int32 ActivationOwner::Read(); ret }",
            ".class public ActivationOwner {",
            ".method public static int32 Read() { ldc.i4.s 42; ret }",
            "}",
            "call ReadOwner");

        Assert.IsTrue(session.DeferActivation);
        Assert.AreEqual(1, session.TypeCount);
        Assert.HasCount(1, session.Methods);
        Assert.AreEqual(42, session.Run().Value);
        Assert.IsFalse(session.DeferActivation);
    }

    /// <summary>
    /// Inspection and export keep type and instance constructors dormant until the explicit cell creates an instance.
    /// </summary>
    [TestMethod]
    public void Run_DefersTypeAndInstanceConstructorsThroughInspectionAndExport()
    {
        var marker = Path.Combine(Path.GetTempPath(), "ilrepl-constructors-" + Guid.NewGuid().ToString("N"));
        try
        {
            var session = new Session { DeferActivation = true };
            var path = JsonSerializer.Serialize(marker);
            Submit(session,
                ".class public ActivationOwner {",
                ".method static void .cctor() {",
                "ldstr " + path,
                "ldstr \"type\\n\"",
                "call void System.IO.File::AppendAllText(string, string)",
                "ret",
                "}",
                ".method public instance void .ctor() {",
                "ldarg.0",
                "call instance void object::.ctor()",
                "ldstr " + path,
                "ldstr \"instance\\n\"",
                "call void System.IO.File::AppendAllText(string, string)",
                "ret",
                "}",
                "}",
                ".method object Create() { newobj instance void ActivationOwner::.ctor(); ret }",
                "call Create");

            Assert.IsFalse(File.Exists(marker));
            Assert.Contains("ActivationOwner", session.ToIlAsm());
            Assert.IsNotEmpty(AssemblyExporter.Write(session, "deferred-constructor-export"));
            Assert.IsFalse(File.Exists(marker), "Inspecting or exporting reconstructed source ran a constructor.");
            Assert.IsTrue(session.DeferActivation);

            var first = session.Run().Value;
            Assert.IsNotNull(first);
            Assert.AreEqual("ActivationOwner", first.GetType().Name);
            Assert.AreEqual("type\ninstance\n", File.ReadAllText(marker));
            session.AddLine("call Create");
            var second = session.Run().Value;
            Assert.IsNotNull(second);
            Assert.AreNotSame(first, second);
            Assert.AreEqual("type\ninstance\ninstance\n", File.ReadAllText(marker));
        }
        finally
        {
            File.Delete(marker);
        }
    }

    /// <summary>
    /// Imported edit images retain their module initializers through reconstruction and activate only on execution.
    /// </summary>
    [TestMethod]
    public void Run_DefersImportedEditModuleInitialization()
    {
        var marker = Path.Combine(Path.GetTempPath(), "ilrepl-edit-activation-" + Guid.NewGuid().ToString("N"));
        try
        {
            var session = new Session { DeferActivation = true };
            session.Resolver.LoadImage(ModuleInitializerFixture.Create(true, marker));
            var edit = session.PrepareEdit("int32 Owner::Read()", "Copy");
            session.CommitEdit(edit.Name, edit.Source);
            session.AddLine("call Copy");
            Assert.Contains("Copy", session.ToIlAsm());
            Assert.IsNotEmpty(AssemblyExporter.Write(session, "deferred-edit-export"));
            Assert.AreEqual("Copy", ComparisonCapture.Create(session, "Copy ()").Name);
            Assert.IsFalse(File.Exists(marker), "Reconstructing or inspecting an edit ran its module initializer.");

            Assert.AreEqual(142, session.Run().Value);
            Assert.AreEqual("initialized\n", File.ReadAllText(marker));
            session.AddLine("call Copy");
            Assert.AreEqual(142, session.Run().Value);
            Assert.AreEqual("initialized\n", File.ReadAllText(marker));
        }
        finally
        {
            File.Delete(marker);
        }
    }

    /// <summary>
    /// Deferred argument declarations retain literal text and materialize values only for execution.
    /// </summary>
    [TestMethod]
    public void Run_MaterializesDeferredLiteralArguments()
    {
        var session = new Session { DeferActivation = true };
        Submit(session, ".args (int32 number = 0x2a, string text = \"hello\")", "ldarg number");
        Assert.HasCount(2, session.Cell.Arguments);
        Assert.IsNull(session.Cell.Arguments[0].Value);
        Assert.IsNull(session.Cell.Arguments[1].Value);
        Assert.AreEqual("0x2a", session.Cell.Arguments[0].ValueText);
        Assert.AreEqual("\"hello\"", session.Cell.Arguments[1].ValueText);
        Assert.Contains("number", session.ToIlAsm());

        Assert.AreEqual(42, session.Run().Value);
        session.AddLine("ldarg text");
        Assert.AreEqual("hello", session.Run().Value);
    }

    /// <summary>
    /// Default value-type arguments remain unmaterialized while reopened source is inspected.
    /// </summary>
    [TestMethod]
    public void Run_MaterializesDeferredDefaultValueTypeArgument()
    {
        var session = new Session { DeferActivation = true };
        Submit(session, ".args (valuetype System.DateTime value)", "ldarg value");
        var argument = Assert.ContainsSingle(session.Cell.Arguments);
        Assert.IsNull(argument.Value);
        Assert.AreEqual("default", argument.ValueText);
        Assert.AreEqual(default(DateTime), Assert.IsInstanceOfType<DateTime>(session.Run().Value));
    }

    /// <summary>
    /// An incomplete cell is rejected before it clears the deferred activation boundary.
    /// </summary>
    [TestMethod]
    public void Compile_IncompleteCellDoesNotActivateReconstructedMethods()
    {
        var session = new Session { DeferActivation = true };
        Submit(session, ".method int32 Answer() { ldc.i4.s 42; ret }", "br LATER");
        var error = Assert.ThrowsExactly<ReplException>(() => CellCompiler.Compile(session));
        Assert.Contains("LATER", error.Message);
        Assert.IsTrue(session.DeferActivation);

        Submit(session, "LATER: call Answer");
        Assert.AreEqual(42, session.Run().Value);
        Assert.IsFalse(session.DeferActivation);
    }

    private static void Submit(Session session, params string[] source)
    {
        foreach (var line in IlLines.Expand(source))
        {
            session.AddLine(line);
        }
    }
}
