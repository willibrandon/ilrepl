using System.Runtime.InteropServices;
using ILVerify;
using IlRepl.Engine;
using IlRepl.Engine.Binding;
using IlRepl.Protocol;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Correct native-address argument coercions execute through real IL while remaining explicitly unverifiable.
/// </summary>
[TestClass]
public sealed class NativeByRefCallTests
{
    /// <summary>
    /// The cancellation token for symbolic source analysis.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Every call form passes initialized native memory as a managed reference and observes the callee's write.
    /// </summary>
    /// <param name="operation">The invocation opcode.</param>
    /// <param name="typedPointer">Whether the address is held as an unmanaged pointer instead of a native integer.</param>
    [TestMethod]
    [DataRow("call", false)]
    [DataRow("call", true)]
    [DataRow("callvirt", false)]
    [DataRow("callvirt", true)]
    [DataRow("newobj", false)]
    [DataRow("newobj", true)]
    [DataRow("calli", false)]
    [DataRow("calli", true)]
    public void Call_NativeAddressCoercionExecutesAndReportsUnverifiable(string operation, bool typedPointer)
    {
        var session = new Session();
        var (_, image, fixture) = CecilFixture.Build((module, type) =>
        {
            var defaultConstructor = new MethodDefinition(".ctor", MethodAttributes.Public | MethodAttributes.HideBySig
                | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName, module.TypeSystem.Void);
            type.Methods.Add(defaultConstructor);
            var constructorIl = defaultConstructor.Body.GetILProcessor();
            constructorIl.Emit(OpCodes.Ldarg_0);
            constructorIl.Emit(OpCodes.Call, module.ImportReference(typeof(object).GetConstructor(Type.EmptyTypes)!));
            constructorIl.Emit(OpCodes.Ret);

            var instance = operation is "callvirt" or "newobj";
            var helper = new MethodDefinition(operation == "newobj" ? ".ctor" : "Increment",
                MethodAttributes.Public | MethodAttributes.HideBySig
                | (operation == "newobj" ? MethodAttributes.SpecialName | MethodAttributes.RTSpecialName
                    : instance ? MethodAttributes.Virtual | MethodAttributes.NewSlot : MethodAttributes.Static), module.TypeSystem.Void);
            helper.Parameters.Add(new ParameterDefinition(new ByReferenceType(module.TypeSystem.Int32)));
            type.Methods.Add(helper);
            var helperIl = helper.Body.GetILProcessor();
            if (operation == "newobj")
            {
                helperIl.Emit(OpCodes.Ldarg_0);
                helperIl.Emit(OpCodes.Call, defaultConstructor);
            }

            helperIl.Emit(instance ? OpCodes.Ldarg_1 : OpCodes.Ldarg_0);
            helperIl.Emit(OpCodes.Dup);
            helperIl.Emit(OpCodes.Ldind_I4);
            helperIl.Emit(OpCodes.Ldc_I4_1);
            helperIl.Emit(OpCodes.Add);
            helperIl.Emit(OpCodes.Stind_I4);
            helperIl.Emit(OpCodes.Ret);

            var method = new MethodDefinition("Read", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Int32);
            method.Parameters.Add(new ParameterDefinition(module.TypeSystem.IntPtr));
            method.Body.InitLocals = true;
            method.Body.Variables.Add(new VariableDefinition(typedPointer
                ? new PointerType(module.TypeSystem.Int32) : module.TypeSystem.IntPtr));
            type.Methods.Add(method);
            var il = method.Body.GetILProcessor();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Stloc_0);
            if (operation == "callvirt")
            {
                il.Emit(OpCodes.Newobj, defaultConstructor);
            }

            il.Emit(OpCodes.Ldloc_0);
            if (operation == "calli")
            {
                il.Emit(OpCodes.Ldftn, helper);
                var site = new CallSite(module.TypeSystem.Void);
                site.Parameters.Add(new ParameterDefinition(new ByReferenceType(module.TypeSystem.Int32)));
                il.Emit(OpCodes.Calli, site);
            }
            else
            {
                il.Emit(operation == "callvirt" ? OpCodes.Callvirt : operation == "newobj" ? OpCodes.Newobj : OpCodes.Call, helper);
                if (operation == "newobj")
                {
                    il.Emit(OpCodes.Pop);
                }
            }

            il.Emit(OpCodes.Ldloc_0);
            il.Emit(OpCodes.Ldind_I4);
            il.Emit(OpCodes.Ret);
        }, session.Resolver);
        var original = fixture.GetMethod("Read")!;
        var listing = MethodDisassembler.Disassemble(original, session);
        var call = listing.Entries.Last(entry => entry.Instruction?.Op.Name == operation);
        var diagnostics = StackAnalysis.Diagnostics(listing);
        using var oracle = new IlVerificationOracle();

        if (operation != "calli")
        {
            // Microsoft ILVerification does not implement calli. The real CLR below is
            // the independent execution oracle for that opcode, with no swallowed failure.
            var verifierErrors = oracle.Verify(image);
            Assert.Contains(VerifierError.StackUnexpected, verifierErrors, string.Join(", ", verifierErrors));
            if (typedPointer)
            {
                Assert.Contains(VerifierError.UnmanagedPointer, verifierErrors);
            }
        }
        Assert.DoesNotContain(diagnostic => diagnostic.Kind == AnalysisDiagnosticKind.Error, diagnostics);
        Assert.Contains(diagnostic => diagnostic.Code == "FLOW007" && diagnostic.Kind == AnalysisDiagnosticKind.Unverifiable
            && diagnostic.Location.Offset == call.Offset, diagnostics);
        var memory = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            Marshal.WriteInt32(memory, 41);
            Assert.AreEqual(42, original.Invoke(null, [memory]));
            Assert.AreEqual(42, Marshal.ReadInt32(memory));
            var edit = session.PrepareEdit("int32 N.Fixture::Read(native int)", "NativeCopy");
            session.CommitEdit(edit.Name, edit.Source);
            Marshal.WriteInt32(memory, 41);
            Assert.AreEqual(42, edit.Method!.Invoke(null, [memory]));
            Assert.AreEqual(42, Marshal.ReadInt32(memory));
        }
        finally
        {
            Marshal.FreeHGlobal(memory);
        }
    }

    /// <summary>
    /// Source preview and live source compilation retain the call's unverifiable classification while allowing its correct native argument.
    /// </summary>
    [TestMethod]
    public async Task Call_SourceAndPreviewAgreeOnNativeArgumentCoercion()
    {
        var session = IlLines.Load(".method int32 Read(int32& value) { ldarg.0; ldind.i4; ret }");
        string[] source = [".method int32 Caller() {", "ldc.i4.4", "conv.u", "localloc", "dup", "ldc.i4 42", "stind.i4",
            "call int32 Read(int32&)", "ret", "}"];
        using var editing = new EditingSession(session);

        var preview = await editing.AnalyzeAsync(new AnalysisRequest(source, 8, 0, 1), TestContext.CancellationToken);

        Assert.DoesNotContain(diagnostic => diagnostic.Kind == AnalysisDiagnosticKind.Error, preview.Diagnostics);
        Assert.Contains(diagnostic => diagnostic.Kind == AnalysisDiagnosticKind.Unverifiable && diagnostic.Location.Line == 7,
            preview.Diagnostics);
        foreach (var line in source)
        {
            session.AddLine(line);
        }

        Assert.AreEqual(42, session.Methods.Single(method => method.Signature.Name == "Caller").Version.Body.Invoke(null, null));
    }

    /// <summary>
    /// Values whose stack categories are not native integers remain incompatible with by-reference parameters.
    /// </summary>
    /// <param name="value">An incompatible argument producer.</param>
    [TestMethod]
    [DataRow("ldc.i4.0")]
    [DataRow("ldc.i8 0")]
    [DataRow("ldc.r8 0")]
    [DataRow("ldnull")]
    [DataRow("ldstr \"invalid\"")]
    public void Call_IncompatibleArgumentCategoriesRemainRejected(string value)
    {
        var session = IlLines.Load(".method int32 Read(int32& value) { ldarg.0; ldind.i4; ret }", value);

        var error = Assert.ThrowsExactly<ReplException>(() => session.AddLine("call int32 Read(int32&)"));

        Assert.Contains("call argument 1 needs int32& but found", error.Message);
        Assert.HasCount(1, session.Methods);
    }
}
