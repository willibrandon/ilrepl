using System.Reflection;
using System.Runtime.Loader;
using ILVerify;
using IlRepl.Engine;
using IlRepl.Engine.Binding;
using IlRepl.Protocol;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MethodAttributes = Mono.Cecil.MethodAttributes;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Array element addresses retain their runtime signatures and controlled-mutability rules throughout the REPL.
/// </summary>
[TestClass]
public sealed class ArrayAddressHelpTests
{
    /// <summary>
    /// Supplies cancellation for editing analysis.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Both ordinary and readonly array addresses preview, execute, disassemble, export, and assemble without metadata loss.
    /// </summary>
    /// <param name="readOnly">Whether the address is obtained with the readonly prefix.</param>
    /// <param name="virtualCall">Whether the runtime-provided instance method is invoked with callvirt.</param>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task ArrayAddress_SourceRoundTripsAndExecutes(bool readOnly, bool virtualCall)
    {
        string[] source = [".class public ArrayReader {", ".method public static int32 Read(int32[,] values) {",
            "ldarg values", "ldc.i4.0", "ldc.i4.1", .. readOnly ? new[] { "readonly." } : [],
            $"{(virtualCall ? "callvirt" : "call")} instance int32& int32[,]::Address(int32, int32)",
            "ldind.i4", "ret", "}", "}"];
        var session = new Session();
        using var editing = new EditingSession(session);
        var preview = await editing.AnalyzeAsync(new AnalysisRequest(source, source.Length - 1, 0, 1), TestContext.CancellationToken);

        Assert.DoesNotContain(diagnostic => diagnostic.Kind is AnalysisDiagnosticKind.Error or AnalysisDiagnosticKind.Unverifiable,
            preview.Diagnostics, string.Join("; ", preview.Diagnostics.Select(diagnostic => diagnostic.Message)));
        foreach (var line in source)
        {
            session.AddLine(line);
        }

        var method = session.Types.Single().RuntimeType!.GetMethod("Read")!;
        Assert.AreEqual(42, method.Invoke(null, [new[,] { { 7, 42 } }]));
        var listing = MethodDisassembler.Disassemble(method, session);
        Assert.DoesNotContain(diagnostic => diagnostic.Kind is AnalysisDiagnosticKind.Error or AnalysisDiagnosticKind.Unverifiable,
            StackAnalysis.Diagnostics(listing));
        Assert.AreEqual(readOnly, listing.Entries.Any(entry => entry.Instruction?.Op.Name == "readonly."));
        var il = session.ToIlAsm();
        Assert.Contains("int32& int32[,]::Address(int32, int32)", il);
        foreach (var image in new[] { AssemblyExporter.Write(session, "array-address"), IlasmLocator.Assemble(il) })
        {
            using var oracle = new IlVerificationOracle();
            Assert.IsEmpty(oracle.Verify(image));
            using var assembly = AssemblyDefinition.ReadAssembly(new MemoryStream(image));
            var body = assembly.MainModule.GetType("ArrayReader").Methods.Single(candidate => candidate.Name == "Read").Body;
            var call = body.Instructions.Single(instruction => instruction.OpCode == (virtualCall ? OpCodes.Callvirt : OpCodes.Call));
            var target = Assert.IsInstanceOfType<MethodReference>(call.Operand);
            Assert.AreEqual(2, Assert.IsInstanceOfType<ArrayType>(target.DeclaringType).Rank);
            Assert.AreEqual(MetadataType.Int32, Assert.IsInstanceOfType<ByReferenceType>(target.ReturnType).ElementType.MetadataType);
            Assert.IsTrue(target.HasThis);
            Assert.AreSequenceEqual([MetadataType.Int32, MetadataType.Int32],
                target.Parameters.Select(parameter => parameter.ParameterType.MetadataType));
            Assert.AreEqual(readOnly, body.Instructions.Any(instruction => instruction.OpCode == OpCodes.Readonly));
            var context = new AssemblyLoadContext("array-address", isCollectible: true);
            try
            {
                var loaded = context.LoadFromStream(new MemoryStream(image));
                Assert.AreEqual(42, loaded.GetType("ArrayReader")!.GetMethod("Read")!.Invoke(null, [new[,] { { 7, 42 } }]));
            }
            finally
            {
                context.Unload();
            }
        }
    }

    /// <summary>
    /// Runtime and independent verifier acceptance establish the legal invocation forms for readonly array addresses.
    /// </summary>
    /// <param name="virtualCall">Whether the address operation uses callvirt.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ArrayAddress_ReadonlyInvocationMatchesRuntimeAndVerifier(bool virtualCall)
    {
        var session = new Session();
        var (_, image, fixture) = AddressFixture(session, virtualCall, write: false);
        using var oracle = new IlVerificationOracle();
        Assert.IsEmpty(oracle.Verify(image));
        var method = fixture.GetMethod("Read")!;
        Assert.AreEqual(42, method.Invoke(null, [new[,] { { 42 } }]));
        var diagnostics = StackAnalysis.Diagnostics(MethodDisassembler.Disassemble(method, session));
        Assert.DoesNotContain(diagnostic => diagnostic.Kind is AnalysisDiagnosticKind.Error or AnalysisDiagnosticKind.Unverifiable,
            diagnostics, string.Join("; ", diagnostics.Select(diagnostic => diagnostic.Message)));
    }

    /// <summary>
    /// A duplicated readonly address remains unsuitable for an indirect store while a direct read stays verifiable.
    /// </summary>
    [TestMethod]
    public void ArrayAddress_ReadonlyResultRemainsControlledMutableAfterDup()
    {
        var session = new Session();
        var (_, image, fixture) = AddressFixture(session, virtualCall: false, write: true);
        using var oracle = new IlVerificationOracle();
        Assert.Contains(VerifierError.ReadOnlyIllegalWrite, oracle.Verify(image));
        var listing = MethodDisassembler.Disassemble(fixture.GetMethod("Read")!, session);
        var store = listing.Entries.Single(entry => entry.Instruction?.Op.Name == "stind.i4");
        var diagnostics = StackAnalysis.Diagnostics(listing);
        Assert.DoesNotContain(diagnostic => diagnostic.Kind == AnalysisDiagnosticKind.Error, diagnostics);
        Assert.Contains(diagnostic => diagnostic.Kind == AnalysisDiagnosticKind.Unverifiable
            && diagnostic.Location.Offset == store.Offset, diagnostics);
        Assert.DoesNotContain(diagnostic => diagnostic.Kind == AnalysisDiagnosticKind.Unverifiable
            && diagnostic.Location.Offset != store.Offset, diagnostics);
    }

    /// <summary>
    /// A readonly element address permits covariant reads without allowing ordinary writable array addresses to bypass type checks.
    /// </summary>
    /// <param name="readOnly">Whether the address is obtained with the readonly prefix.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ArrayAddress_ReadonlyAllowsCovariantReadWhileWritableAddressChecksElementType(bool readOnly)
    {
        string[] prefix = readOnly ? ["readonly."] : [];
        var session = IlLines.Load([".method object Read(object[,] values) {", "ldarg values", "ldc.i4.0", "ldc.i4.0",
            .. prefix,
            "call instance object& object[,]::Address(int32, int32)", "ldind.ref", "ret", "}"]);
        var method = session.Methods.Single().Version.Body;
        object[] arguments = [new[,] { { "element" } }];
        if (readOnly)
        {
            Assert.AreEqual("element", method.Invoke(null, arguments));
        }
        else
        {
            var error = Assert.ThrowsExactly<TargetInvocationException>(() => method.Invoke(null, arguments));
            Assert.IsInstanceOfType<ArrayTypeMismatchException>(error.InnerException);
        }
    }

    /// <summary>
    /// A matching method name on an ordinary type and a non-address array operation cannot consume readonly.
    /// </summary>
    /// <param name="arrayGet">Whether the invalid callee is the runtime-provided array Get operation.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ReadonlyPrefix_RejectsNonAddressCalleesInSourceAndPreview(bool arrayGet)
    {
        var session = IlLines.Load(".method int32& Address(int32& value) { ldarg value; ret }");
        string[] source = arrayGet
            ? [".method int32 Read(int32[,] values) {", "ldarg values", "ldc.i4.0", "ldc.i4.0", "readonly.",
                "call instance int32 int32[,]::Get(int32, int32)", "ret", "}"]
            : [".method int32 Read(int32& value) {", "ldarg value", "readonly.",
                "call int32& Address(int32&)", "ldind.i4", "ret", "}"];
        using var editing = new EditingSession(session);
        var preview = await editing.AnalyzeAsync(new AnalysisRequest(source, source.Length - 1, 0, 1), TestContext.CancellationToken);
        Assert.Contains(diagnostic => diagnostic.Code == "FLOW019" && diagnostic.Kind == AnalysisDiagnosticKind.Error
            && diagnostic.Message == "readonly. cannot prefix call", preview.Diagnostics);
        var error = Assert.ThrowsExactly<ReplException>(() =>
        {
            foreach (var line in source)
            {
                session.AddLine(line);
            }
        });
        Assert.Contains("readonly. cannot prefix call", error.Message);
        Assert.HasCount(1, session.Methods);
    }

    /// <summary>
    /// Runtime-provided signatures preserve array element types without attempting to read nonexistent MethodDef rows.
    /// </summary>
    /// <param name="arrayType">The owning array construction.</param>
    [TestMethod]
    [DataRow(typeof(int[]))]
    [DataRow(typeof(int[,]))]
    [DataRow(typeof(string[,,]))]
    [DataRow(typeof(int[][,]))]
    [DataRow(typeof(int[][][]))]
    public void ArrayMembers_SignaturesPreserveElementAndIndexTypes(Type arrayType)
    {
        var methods = arrayType.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public);
        Assert.AreSequenceEqual(["Address", "Get", "Set"], methods.Select(method => method.Name).Order(StringComparer.Ordinal));
        Assert.HasCount(methods.Length, methods.Select(RuntimeDefinitions.Of).Distinct().ToArray());
        var symbols = SymbolArrayMethods.Create(RuntimeSymbolImporter.Import(arrayType));
        Assert.HasCount(symbols.Count, symbols.Select(symbol => symbol.Definition).Distinct().ToArray());
        foreach (var method in methods)
        {
            var signature = RuntimeMetadataSignatures.Read(method);
            Assert.IsTrue(signature.HasThis);
            Assert.AreEqual(method.ReturnType, signature.ReturnType.ToClrType());
            Assert.AreSequenceEqual(method.GetParameters().Select(parameter => parameter.ParameterType),
                signature.Parameters.Select(parameter => parameter.ToClrType()));
            Assert.AreEqual(method.Name == "Address", RuntimeArrayMethods.IsAddress(method));
            Assert.AreEqual(method.Name == "Address", RuntimeArrayMethods.IsAddress(RuntimeSymbolImporter.Import(method)));
            var symbol = Assert.ContainsSingle(symbols.Where(candidate => candidate.Name == method.Name));
            Assert.IsTrue(SymbolIdentity.Equal(RuntimeSymbolImporter.Import(method.ReturnType), symbol.ReturnType));
            Assert.AreSequenceEqual(method.GetParameters().Select(parameter => RuntimeSymbolImporter.Import(parameter.ParameterType)),
                symbol.ParameterTypes);
            Assert.AreEqual(method.Name == "Address", RuntimeArrayMethods.IsAddress(symbol));
        }

        var constructors = arrayType.GetConstructors();
        Assert.IsNotEmpty(constructors);
        Assert.HasCount(constructors.Length, symbols.Where(symbol => symbol.IsConstructor).ToArray());
        foreach (var constructor in constructors)
        {
            var signature = RuntimeMetadataSignatures.Read(constructor);
            Assert.IsTrue(signature.HasThis);
            Assert.AreEqual(typeof(void), signature.ReturnType.ToClrType());
            Assert.AreSequenceEqual(constructor.GetParameters().Select(parameter => parameter.ParameterType),
                signature.Parameters.Select(parameter => parameter.ToClrType()));
            var symbol = Assert.ContainsSingle(symbols.Where(candidate => candidate.IsConstructor
                && candidate.Parameters.Count == constructor.GetParameters().Length));
            Assert.IsTrue(SymbolIdentity.Equal(TypeSymbol.Void, symbol.ReturnType));
            Assert.AreSequenceEqual(constructor.GetParameters().Select(parameter => RuntimeSymbolImporter.Import(parameter.ParameterType)),
                symbol.ParameterTypes);
        }
    }

    /// <summary>
    /// Array Address signatures refer to the caller's generic owner rather than a nonexistent array method definition.
    /// </summary>
    /// <param name="methodParameter">Whether the array element is a method parameter rather than a type parameter.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ArrayAddress_GenericElementImportsInEnclosingOwnerContext(bool methodParameter)
    {
        var writer = new CecilWriter("generic-array-address");
        using var assembly = writer.Assembly;
        var owner = writer.DefineType("", "Owner", Mono.Cecil.TypeAttributes.Public, writer.Module.TypeSystem.Object);
        var method = new MethodDefinition("Read", MethodAttributes.Public | MethodAttributes.Static, writer.Module.TypeSystem.Void);
        owner.Methods.Add(method);
        var source = methodParameter ? typeof(Enumerable).GetMethod(nameof(Enumerable.Empty))! : (MemberInfo)typeof(List<>);
        var element = source is MethodInfo genericMethod ? genericMethod.GetGenericArguments()[0]
            : ((Type)source).GetGenericArguments()[0];
        var parameter = new GenericParameter("T", methodParameter ? method : owner);
        if (methodParameter)
        {
            method.GenericParameters.Add(parameter);
        }
        else
        {
            owner.GenericParameters.Add(parameter);
        }

        writer.Define(element, parameter);
        var address = element.MakeArrayType(2).GetMethod("Address")!;
        var imported = writer.Import(address);

        Assert.AreSame(parameter, Assert.IsInstanceOfType<ArrayType>(imported.DeclaringType).ElementType);
        Assert.AreSame(parameter, Assert.IsInstanceOfType<ByReferenceType>(imported.ReturnType).ElementType);
        Assert.AreSequenceEqual([MetadataType.Int32, MetadataType.Int32],
            imported.Parameters.Select(argument => argument.ParameterType.MetadataType));
        Assert.IsTrue(imported.HasThis);
        Assert.IsEmpty(imported.GenericParameters);
    }

    private static (Assembly Assembly, byte[] Image, Type Fixture) AddressFixture(Session session, bool virtualCall, bool write) =>
        CecilFixture.Build((module, type) =>
        {
            var array = new ArrayType(module.TypeSystem.Int32, 2);
            var address = new MethodReference("Address", new ByReferenceType(module.TypeSystem.Int32), array) { HasThis = true };
            address.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int32));
            address.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int32));
            var method = new MethodDefinition("Read", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Int32);
            method.Parameters.Add(new ParameterDefinition(array));
            type.Methods.Add(method);
            var il = method.Body.GetILProcessor();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Readonly);
            il.Emit(virtualCall ? OpCodes.Callvirt : OpCodes.Call, address);
            if (write)
            {
                il.Emit(OpCodes.Dup);
                il.Emit(OpCodes.Ldc_I4, 43);
                il.Emit(OpCodes.Stind_I4);
            }

            il.Emit(OpCodes.Ldind_I4);
            il.Emit(OpCodes.Ret);
        }, session.Resolver);
}
