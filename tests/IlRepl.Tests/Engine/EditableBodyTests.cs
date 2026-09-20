using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
using IlRepl.Engine;
using IlRepl.Engine.Binding;
using IlRepl.Protocol;
using Mono.Cecil;
using Mono.Cecil.Cil;
using CecilMethodDefinition = Mono.Cecil.MethodDefinition;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Editable bodies preserve headers and exact exception tables through execution, export, and independent Microsoft ILAsm.
/// </summary>
[TestClass]
public sealed class EditableBodyTests
{
    /// <summary>
    /// The test cancellation token for source preview requests.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// The initialization bit survives compilation and both export formats, including bodies without local slots.
    /// </summary>
    /// <param name="directive">The local declaration, or null to retain the default.</param>
    /// <param name="initialized">The expected method-header initialization flag.</param>
    /// <param name="slots">The expected number of local variables.</param>
    [TestMethod]
    [DataRow(null, true, 0)]
    [DataRow(".locals init ()", true, 0)]
    [DataRow(".locals ()", false, 0)]
    [DataRow(".locals init (  )", true, 0)]
    [DataRow(".locals (  )", false, 0)]
    [DataRow(".locals init (int32 v)", true, 1)]
    [DataRow(".locals (int32 v)", false, 1)]
    public void LocalsDirective_PreservesInitializationInRuntimeAndExports(string? directive, bool initialized, int slots)
    {
        var lines = new List<string> { ".method int32 M() {" };
        if (directive is not null)
        {
            lines.Add(directive);
        }

        lines.Add("ldc.i4.s 42");
        if (slots != 0)
        {
            // Never read an uninitialized slot: this test checks metadata, not indeterminate memory.
            lines.Add("stloc.0");
            lines.Add("ldloc.0");
        }

        lines.Add("ret");
        lines.Add("}");
        var session = IlLines.Load([.. lines]);
        var method = session.Methods.Single();
        Assert.AreEqual(initialized, method.State.InitLocals);
        Assert.HasCount(slots, method.State.Locals);
        Assert.AreEqual(initialized, method.Version.Body.GetMethodBody()!.InitLocals);
        Assert.AreEqual(42, method.Version.Body.Invoke(null, null));

        foreach (var image in Images(session))
        {
            using var moduleStream = new MemoryStream(image);
            using var module = ModuleDefinition.ReadModule(moduleStream);
            var body = FindMethod(module).Body;
            Assert.AreEqual(initialized, body.InitLocals, module.Name);
            Assert.HasCount(slots, body.Variables);
            Assert.AreEqual(42, Invoke(image));
        }
    }

    /// <summary>
    /// Declared type members use the same initialization semantics as standalone methods.
    /// </summary>
    /// <param name="directive">The empty local declaration.</param>
    /// <param name="initialized">The expected method-header initialization flag.</param>
    [TestMethod]
    [DataRow(".locals init ()", true)]
    [DataRow(".locals ()", false)]
    public void LocalsDirective_TypeMemberPreservesEmptyLocalInitialization(string directive, bool initialized)
    {
        var session = IlLines.Load(".class public Holder {", ".method public static int32 M() {",
            directive, "ldc.i4.s 42", "ret", "}", "}");
        foreach (var image in new[]
        {
            AssemblyExporter.Write(session, "member-init"),
            IlasmLocator.Assemble(IlAsmRenderer.Render(session)),
        })
        {
            using var moduleStream = new MemoryStream(image);
            using var module = ModuleDefinition.ReadModule(moduleStream);
            var method = module.Types.Single(type => type.Name == "Holder").Methods.Single(method => method.Name == "M");
            Assert.AreEqual(initialized, method.Body.InitLocals);
            Assert.IsEmpty(method.Body.Variables);
            Assert.AreEqual(42, Invoke(image, typeName: "Holder"));
        }

        session.AddLine("call int32 Holder::M()");
        Assert.AreEqual(42, session.Run().Value);
    }

    /// <summary>
    /// Cell compilation and rebuilding preserve initialization, while reset restores the initialized default.
    /// </summary>
    /// <param name="directive">The empty local declaration.</param>
    /// <param name="initialized">The expected initialization flag before reset.</param>
    [TestMethod]
    [DataRow(".locals init ()", true)]
    [DataRow(".locals ()", false)]
    public void LocalsDirective_CellCompilationAndResetPreserveInitialization(string directive, bool initialized)
    {
        var session = IlLines.Load(directive, "ldc.i4.s 42");
        var compiled = CellCompiler.Compile(session);
        try
        {
            Assert.AreEqual(initialized, compiled.EntryPoint.GetMethodBody()!.InitLocals);
            Assert.AreEqual(42, compiled.Invoke(null));
        }
        finally
        {
            compiled.Release();
        }

        session.ClearCell();
        Assert.AreEqual(initialized, session.State.InitLocals);
        session.Reset();
        Assert.IsTrue(session.State.InitLocals);
        Assert.IsEmpty(session.State.Locals);
    }

    /// <summary>
    /// The declared maximum stack is preserved as a floor and grows when executable instructions require more depth.
    /// </summary>
    /// <param name="declared">The authored maximum-stack floor.</param>
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(32)]
    [DataRow(65535)]
    public void MaxStack_UsesDeclaredFloorAndComputedDepth(int declared)
    {
        var session = IlLines.Load(".method int32 M() {", ".locals init ()",
            ".maxstack " + declared.ToString(CultureInfo.InvariantCulture), "ldc.i4.s 19", "ldc.i4.s 23", "add", "ret", "}");
        Assert.AreEqual(declared, session.Methods.Single().State.DeclaredMaxStack);
        foreach (var image in Images(session))
        {
            using var moduleStream = new MemoryStream(image);
            using var module = ModuleDefinition.ReadModule(moduleStream);
            var body = FindMethod(module).Body;
            Assert.IsGreaterThanOrEqualTo(Math.Max(2, declared), body.MaxStackSize);
            Assert.AreEqual(42, Invoke(image));
        }
    }

    /// <summary>
    /// Values outside the method header's unsigned 16-bit stack range are refused immediately.
    /// </summary>
    /// <param name="value">The invalid maximum-stack operand.</param>
    [TestMethod]
    [DataRow("-1")]
    [DataRow("65536")]
    [DataRow("many")]
    public void MaxStack_InvalidDeclarationLeavesBodyUnchanged(string value)
    {
        var session = IlLines.Load(".method int32 M() {", ".maxstack 4");
        var exception = Assert.ThrowsExactly<ReplException>(() => session.AddLine(".maxstack " + value));
        Assert.Contains("maxstack", exception.Message);
        Assert.AreEqual(4, session.State.DeclaredMaxStack);
        session.AddLine("ldc.i4.s 42");
        session.AddLine("ret");
        session.AddLine("}");
        Assert.AreEqual(42, session.Methods.Single().Version.Body.Invoke(null, null));
    }

    /// <summary>
    /// A handler separated from its try region preserves its gaps, exact boundaries and both the exception path and the normal path.
    /// </summary>
    [TestMethod]
    public void RangeCatch_NoncontiguousBodyMatchesIndependentIlasm()
    {
        const string body = """
            .maxstack 2
            .locals init (int32 v)
            .try TRY to GAP catch [System.Runtime]System.Exception handler CATCH to DONE
            TRY: ldarg.0
            brfalse NORMAL
            newobj instance void [System.Runtime]System.ArgumentException::.ctor()
            throw
            NORMAL: ldc.i4.7
            stloc.0
            leave DONE
            GAP: nop
            br DONE
            CATCH: pop
            ldc.i4.s 42
            stloc.0
            leave DONE
            DONE: ldloc.0
            ret
            """;
        AssertRangeBody(body, (0, 7), (1, 42));
    }

    /// <summary>
    /// Catch dispatch follows the authored metadata order even when the handlers occur in the opposite order in the instruction stream.
    /// </summary>
    [TestMethod]
    public void RangeCatch_MetadataOrderControlsDispatch()
    {
        const string body = """
            .maxstack 2
            .locals init (int32 v)
            .try TRY to ARGUMENT catch [System.Runtime]System.Exception handler EXCEPTION to DONE
            .try TRY to ARGUMENT catch [System.Runtime]System.ArgumentException handler ARGUMENT to EXCEPTION
            TRY: newobj instance void [System.Runtime]System.ArgumentException::.ctor()
            throw
            ARGUMENT: pop
            ldc.i4.1
            stloc.0
            leave DONE
            EXCEPTION: pop
            ldc.i4.2
            stloc.0
            leave DONE
            DONE: ldloc.0
            ret
            """;
        var session = AssertRangeBody(body, (0, 2));
        var listing = MethodDisassembler.Disassemble(session.Methods.Single().Version.Body, session);
        Assert.AreSequenceEqual(new[] { typeof(Exception), typeof(ArgumentException) }, listing.Clauses.Select(clause => clause.CatchType));
        Assert.IsGreaterThan(listing.Clauses[1].HandlerStart, listing.Clauses[0].HandlerStart);
    }

    /// <summary>
    /// Filters receive the exception object, select the appropriate handler, and preserve exact clause boundaries.
    /// </summary>
    [TestMethod]
    public void RangeFilter_SelectsHandlerAndPreservesBoundaries()
    {
        const string body = """
            .maxstack 2
            .locals init (int32 v)
            .try TRY to FILTER filter FILTER handler ACCEPTED to FALLBACK
            .try TRY to FILTER catch [System.Runtime]System.Exception handler FALLBACK to DONE
            TRY: newobj instance void [System.Runtime]System.ArgumentException::.ctor()
            throw
            FILTER: pop
            ldarg.0
            endfilter
            ACCEPTED: pop
            ldc.i4.s 42
            stloc.0
            leave DONE
            FALLBACK: pop
            ldc.i4.7
            stloc.0
            leave DONE
            DONE: ldloc.0
            ret
            """;
        AssertRangeBody(body, (0, 7), (1, 42));
    }

    /// <summary>
    /// Finally and fault mutate real local state only on their specified normal and exceptional exit paths.
    /// </summary>
    /// <param name="kind">The handler kind, finally or fault.</param>
    /// <param name="normalResult">The expected return when the protected body does not throw.</param>
    [TestMethod]
    [DataRow("finally", 12)]
    [DataRow("fault", 10)]
    public void RangeUnwind_ExecutesOnlyForAppropriateExit(string kind, int normalResult)
    {
        var body = $$"""
            .maxstack 2
            .locals init (int32 v)
            .try TRY to UNWIND {{kind}} handler UNWIND to CATCH
            .try TRY to CATCH catch [System.Runtime]System.Exception handler CATCH to DONE
            TRY: ldc.i4.s 10
            stloc.0
            ldarg.0
            brfalse NORMAL
            newobj instance void [System.Runtime]System.ArgumentException::.ctor()
            throw
            NORMAL: leave DONE
            UNWIND: ldloc.0
            ldc.i4.2
            add
            stloc.0
            endfinally
            CATCH: pop
            ldloc.0
            ldc.i4.s 100
            add
            stloc.0
            leave DONE
            DONE: ldloc.0
            ret
            """;
        AssertRangeBody(body, (0, normalResult), (1, 112));
    }

    /// <summary>
    /// An exclusive boundary at code size preserves the final endfinally without adding executable instructions.
    /// </summary>
    [TestMethod]
    public void RangeFinally_EndAtCodeSizeDoesNotAddInstruction()
    {
        const string body = """
            .maxstack 2
            .locals init (int32 v)
            .try TRY to DONE finally handler FINALLY to END
            TRY: ldc.i4.s 40
            stloc.0
            leave DONE
            DONE: ldloc.0
            ret
            FINALLY: ldloc.0
            ldc.i4.2
            add
            stloc.0
            endfinally
            END:
            """;
        var session = AssertRangeBody(body, (0, 42));
        foreach (var image in Images(session))
        {
            AssertEmitted(image);
        }

        static void AssertEmitted(byte[] image)
        {
            using var moduleStream = new MemoryStream(image);
            using var module = ModuleDefinition.ReadModule(moduleStream);
            var emitted = FindMethod(module).Body;
            Assert.IsNull(emitted.ExceptionHandlers.Single().HandlerEnd);
            Assert.AreEqual(Code.Endfinally, emitted.Instructions[^1].OpCode.Code);
            Assert.HasCount(10, emitted.Instructions);
        }
    }

    /// <summary>
    /// Undefined, empty and reversed ranges cannot replace a previously committed method.
    /// </summary>
    /// <param name="tryStart">The protected range's starting label.</param>
    /// <param name="tryEnd">The protected range's exclusive ending label.</param>
    /// <param name="handlerStart">The handler's starting label.</param>
    /// <param name="handlerEnd">The handler's exclusive ending label.</param>
    /// <param name="diagnosticLabel">The invalid boundary named by the diagnostic.</param>
    [TestMethod]
    [DataRow("TRY", "MISSING", "HANDLER", "DONE", "MISSING")]
    [DataRow("TRY", "TRY", "HANDLER", "DONE", "TRY")]
    [DataRow("HANDLER", "TRY", "HANDLER", "DONE", "TRY")]
    [DataRow("TRY", "HANDLER", "HANDLER", "HANDLER", "HANDLER")]
    [DataRow("TRY", "HANDLER", "DONE", "HANDLER", "HANDLER")]
    public void RangeClause_InvalidBoundariesRejectCommit(
        string tryStart,
        string tryEnd,
        string handlerStart,
        string handlerEnd,
        string diagnosticLabel)
    {
        var session = IlLines.Load(".method int32 M() { ldc.i4.s 42; ret }");
        var original = session.Methods.Single().Version.Body;
        var source = new[]
        {
            ".method int32 M() {",
            ".locals init (int32 v)",
            $".try {tryStart} to {tryEnd} catch [System.Runtime]System.Exception handler {handlerStart} to {handlerEnd}",
            "TRY: ldc.i4.1", "stloc.0", "leave DONE",
            "HANDLER: pop", "leave DONE",
            "DONE: ldloc.0", "}",
        };

        var exception = Assert.ThrowsExactly<ReplException>(() =>
        {
            foreach (var line in source)
            {
                session.AddLine(line);
            }
        });

        Assert.Contains(diagnosticLabel, exception.Message);
        Assert.AreSame(original, session.Methods.Single().Version.Body);
        Assert.AreEqual(42, original.Invoke(null, null));
    }

    /// <summary>
    /// Editor range clauses seed handler stacks and locate boundary errors without creating live session methods.
    /// </summary>
    /// <returns>A task that completes after both preview states are checked.</returns>
    [TestMethod]
    public async Task RangePreview_SeedsHandlerAndReportsInvalidBoundaryWithoutCommitting()
    {
        var session = new Session();
        using var editing = new EditingSession(session);
        string[] source =
        [
            ".method int32 M() {",
            ".locals init (int32 v)",
            ".try TRY to HANDLER catch [System.Runtime]System.Exception handler HANDLER to DONE",
            "TRY: newobj instance void [System.Runtime]System.Exception::.ctor()",
            "throw",
            "HANDLER: pop",
            "ldc.i4.s 42",
            "stloc.0",
            "leave DONE",
            "DONE: ldloc.0",
            "ret",
            "}",
        ];
        var handler = await editing.AnalyzeAsync(new AnalysisRequest(source, 5, 9, 1), TestContext.CancellationToken);
        Assert.AreEqual(AnalyzedStackKind.Known, handler.Stack!.Kind);
        Assert.AreEqual(1, handler.Stack.Depth);
        Assert.DoesNotContain(diagnostic => diagnostic.Kind == AnalysisDiagnosticKind.Error, handler.Diagnostics);
        var afterPop = await editing.AnalyzeAsync(new AnalysisRequest(source, 6, 0, 2), TestContext.CancellationToken);
        Assert.AreEqual("[]", afterPop.Stack!.Render());

        source[2] = ".try TRY to TRY catch [System.Runtime]System.Exception handler HANDLER to DONE";
        var invalid = await editing.AnalyzeAsync(new AnalysisRequest(source, 5, 9, 3), TestContext.CancellationToken);
        var error = invalid.Diagnostics.Single(diagnostic => diagnostic.Code == "FLOW026");
        Assert.AreEqual(2, error.Location.Line);
        Assert.AreEqual(AnalysisDiagnosticKind.Error, error.Kind);
        Assert.Contains("TRY", error.Message);
        Assert.IsEmpty(session.Methods);
        Assert.IsTrue(session.Cell.IsEmpty);
    }

    /// <summary>
    /// Explicit catch ranges nest inside a structured finally and preserve both dispatch and unwind behavior.
    /// </summary>
    [TestMethod]
    public void RangeCatch_InsideStructuredFinallyPreservesBothExceptionModels()
    {
        const string body = """
            .maxstack 2
            .locals init (int32 result)
            .try {
                .try INNER to HANDLER catch [System.Runtime]System.Exception handler HANDLER to CONTINUE
                INNER: newobj instance void [System.Runtime]System.Exception::.ctor()
                throw
                HANDLER: pop
                ldc.i4.7
                stloc.0
                leave CONTINUE
                CONTINUE: leave DONE
            } finally {
                ldloc.0
                ldc.i4.1
                add
                stloc.0
            }
            DONE: ldloc.0
            ret
            """;

        var session = IlLines.Load([".method int32 M(int32 n) {", .. BodyLines(body), "}"]);
        Assert.AreEqual(8, session.Methods.Single().Version.Body.Invoke(null, [0]));
        var images = Images(session).ToArray();
        using var expectedModuleStream = new MemoryStream(images[0]);
        using var expectedModule = ModuleDefinition.ReadModule(expectedModuleStream);
        foreach (var image in images)
        {
            using var moduleStream = new MemoryStream(image);
            using var module = ModuleDefinition.ReadModule(moduleStream);
            var method = FindMethod(module);
            Assert.AreSequenceEqual(new[] { ExceptionHandlerType.Catch, ExceptionHandlerType.Finally },
                method.Body.ExceptionHandlers.Select(handler => handler.HandlerType));
            CecilOracle.AssertSameMeaning(FindMethod(expectedModule), method, "self", "self");
            using var verifier = new IlVerificationOracle();
            Assert.IsEmpty(verifier.Verify(image));
            Assert.AreEqual(8, Invoke(image, [0]));
        }
    }

    private static Session AssertRangeBody(string body, params (int Input, int Expected)[] cases)
    {
        var session = IlLines.Load([".method int32 M(int32 n) {", .. BodyLines(body), "}"]);
        // Microsoft ILAsm resolves range labels immediately, so declarations follow the body.
        // The engine deliberately accepts them before the instructions to support live analysis.
        var lines = BodyLines(body);
        var independentBody = string.Join('\n', lines.Where(line => !IsRangeClause(line))
            .Concat(lines.Where(IsRangeClause)));
        var independent = IlasmLocator.Assemble($$"""
            .assembly extern System.Runtime { }
            .assembly IndependentEditableBody { }
            .module IndependentEditableBody.dll
            .class public abstract sealed IlRepl.Cell extends [System.Runtime]System.Object {
                .method public static int32 M(int32 n) cil managed {
                    {{independentBody}}
                }
            }
            """);
        using var expectedModuleStream = new MemoryStream(independent);
        using var expectedModule = ModuleDefinition.ReadModule(expectedModuleStream);
        var expected = FindMethod(expectedModule);
        using (var verifier = new IlVerificationOracle())
        {
            Assert.IsEmpty(verifier.Verify(independent), "independently authored ILAsm body");
        }

        foreach (var (input, result) in cases)
        {
            Assert.AreEqual(result, Invoke(independent, [input]), "independent Microsoft ILAsm fixture");
            Assert.AreEqual(result, session.Methods.Single().Version.Body.Invoke(null, [input]), "live body");
        }

        foreach (var image in Images(session))
        {
            using var moduleStream = new MemoryStream(image);
            using var module = ModuleDefinition.ReadModule(moduleStream);
            CecilOracle.AssertSameMeaning(expected, FindMethod(module), "self", "self");
            using var verifier = new IlVerificationOracle();
            Assert.IsEmpty(verifier.Verify(image), "Microsoft ILVerification");
            foreach (var (input, result) in cases)
            {
                Assert.AreEqual(result, Invoke(image, [input]), module.Name);
            }
        }

        return session;
    }

    private static bool IsRangeClause(string line) => line.StartsWith(".try ", StringComparison.Ordinal)
        && line.Contains(" to ", StringComparison.Ordinal);

    private static string[] BodyLines(string source) => source.Split('\n',
        StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static IEnumerable<byte[]> Images(Session session)
    {
        var image = session.Methods.Single().Version.Definition.Image;
        Assert.IsNotNull(image, "the committed method retains its emitted image");
        yield return image;
        yield return AssemblyExporter.Write(session, "editable-body");
        yield return IlasmLocator.Assemble(IlAsmRenderer.Render(session));
    }

    private static CecilMethodDefinition FindMethod(ModuleDefinition module) =>
        module.Types.Single(type => type.FullName == "IlRepl.Cell").Methods.Single(method => method.Name == "M");

    private static object? Invoke(byte[] image, object?[]? arguments = null, string typeName = "IlRepl.Cell")
    {
        var context = new AssemblyLoadContext("editable-body-" + Guid.NewGuid().ToString("N"), isCollectible: true);
        try
        {
            var assembly = context.LoadImage(image);
            return assembly.GetType(typeName, throwOnError: true)!.GetMethod("M",
                BindingFlags.Public | BindingFlags.Static)!.Invoke(null, arguments);
        }
        finally
        {
            context.Unload();
        }
    }
}
