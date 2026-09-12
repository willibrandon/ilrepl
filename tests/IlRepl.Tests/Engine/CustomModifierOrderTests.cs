using System.Runtime.CompilerServices;
using IlRepl.Engine;
using IlRepl.Engine.Binding;
using Mono.Cecil;
using Mono.Cecil.Cil;
using GenericParameterAttributes = System.Reflection.GenericParameterAttributes;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Custom modifiers keep their joint metadata order through every declaration and replay path.
/// </summary>
[TestClass]
public sealed class CustomModifierOrderTests
{
    private const string Volatile = "[System.Runtime]System.Runtime.CompilerServices.IsVolatile";
    private const string Long = "[System.Runtime.CompilerServices.VisualC]System.Runtime.CompilerServices.IsLong";
    private const string Cdecl = "[System.Runtime]System.Runtime.CompilerServices.CallConvCdecl";
    private static readonly string ReturnType = $"int32 modreq({Volatile}) modopt({Long}) modreq({Cdecl})";
    private static readonly string ParameterType = $"int32 modopt({Cdecl}) modreq({Volatile}) modopt({Long})";
    private const string PrettyReturnType = "int32 modreq(IsVolatile) modopt(IsLong) modreq(CallConvCdecl)";
    private const string PrettyParameterType = "int32 modopt(CallConvCdecl) modreq(IsVolatile) modopt(IsLong)";
    private static readonly string[] ReturnModifiers = ["modreq:IsVolatile", "modopt:IsLong", "modreq:CallConvCdecl"];
    private static readonly string[] ParameterModifiers = ["modopt:CallConvCdecl", "modreq:IsVolatile", "modopt:IsLong"];

    /// <summary>
    /// The test runner's cancellation and reporting context.
    /// </summary>
    public required TestContext TestContext { get; set; }

    /// <summary>
    /// Session and class methods retain interleaved modifiers in live, listed, and exported signatures.
    /// </summary>
    [TestMethod]
    public void Declarations_InterleavedModifiers_PreserveTheirOrderEverywhere()
    {
        var session = IlLines.Load(
            $".method {ReturnType} Echo({ParameterType} value) {{ ldarg value; ret }}",
            ".class public Holder {",
            $".method public static {ReturnType} Echo({ParameterType} value) {{ ldarg value; ret }}",
            "}");
        var method = session.Methods.Single();

        Assert.AreEqual($"{PrettyReturnType} Echo({PrettyParameterType})", method.Signature.Describe());
        Assert.AreEqual($"{PrettyReturnType} Echo({PrettyParameterType} value)", method.Signature.DescribeWithNames());

        AssertMethod(method.Trampoline.Definition.Image!, "IlRepl.Cell", "Echo");
        AssertMethod(method.Trampoline.Definition.Image!, "IlRepl.Cell/EchoDelegate", "Invoke");
        AssertMethod(method.Version.Definition.Image!, "IlRepl.Cell", "Echo");
        AssertMethod(session.Types.Single().Definition!.Image!, "Holder", "Echo");

        var il = session.ToIlAsm();
        Assert.Contains($"{ReturnType} Echo({ParameterType} 'value')", il);
        _ = IlasmLocator.Assemble(il);

        var exported = AssemblyExporter.Write(session, "modifier-order");
        AssertMethod(exported, "IlRepl.Cell", "Echo");
        AssertMethod(exported, "Holder", "Echo");
    }

    /// <summary>
    /// Replacing a modifier type rebuilds the method against the new identity without changing order.
    /// </summary>
    [TestMethod]
    public void Replacement_TypeUsedAsAnInterleavedModifier_RebuildsTheMethodExactly()
    {
        var session = IlLines.Load(
            ".class public Marker { }",
            $".method int32 modreq({Volatile}) modopt(Marker) modreq({Long}) F() {{ ldc.i4 7; ret }}");
        var previous = session.Methods.Single().Trampoline;

        session.AddLine(".class public Marker {");
        session.AddLine(".field public int32 Value");
        var result = session.AddLine("}");

        Assert.Contains("rebuilt method F", result.Message!);
        var current = session.Methods.Single();
        Assert.AreNotSame(previous, current.Trampoline);
        Assert.AreSame(
            session.Types.Single().RuntimeType,
            current.Trampoline.Method.ReturnParameter.GetOptionalCustomModifiers().Single());
        AssertModifiers(
            Method(current.Trampoline.Definition.Image!, "IlRepl.Cell", "F").ReturnType,
            ["modreq:IsVolatile", "modopt:Marker", "modreq:IsLong"]);
        _ = AssemblyExporter.Write(session, "modifier-replacement");
    }

    /// <summary>
    /// Standalone call signatures and session call operands retain their complete annotated types when read back.
    /// </summary>
    [TestMethod]
    public void BodyOperands_AnnotatedSignatures_RoundTripExactly()
    {
        var exact = $"int32 modopt({Long}) modreq({Volatile})";
        var session = IlLines.Load(
            $".method {exact} Target() {{ ldc.i4 7; ret }}",
            ".method int32 Caller() {",
            $"ldftn {exact} Target()",
            $"calli {exact}()",
            "ret",
            "}");

        var caller = session.Methods.Single(method => method.Signature.Name == "Caller");
        var listing = MethodDisassembler.Disassemble(caller.Version.Body, session);
        var text = string.Join("\n", DisassemblyText.Lines(listing));
        Assert.Contains($"ldftn {exact} Target()", text);
        Assert.Contains($"calli {exact}()", text);
        Assert.Contains($"calli {exact}()", session.ToIlAsm());
        _ = IlasmLocator.Assemble(session.ToIlAsm());

        session.ClearCell();
        var error = Assert.ThrowsExactly<ReplException>(() =>
            session.AddLine($"call int32 modreq({Cdecl}) Target()"));
        Assert.Contains("returns int32 modopt(IsLong) modreq(IsVolatile)", error.Message);
    }

    /// <summary>
    /// Field declarations retain interleaved modifiers in their live and exported metadata.
    /// </summary>
    [TestMethod]
    public void Field_InterleavedModifiers_PreservesTheirOrder()
    {
        var session = IlLines.Load(
            ".class public Fields {",
            $".field public static {ReturnType} Value",
            "}");

        AssertModifiers(
            MethodType(session.Types.Single().Definition!.Image!, "Fields", "Value"),
            ReturnModifiers);
        Assert.Contains($".field public static {ReturnType} Value", session.ToIlAsm());
        var exported = AssemblyExporter.Write(session, "field-modifier-order");
        AssertModifiers(MethodType(exported, "Fields", "Value"), ReturnModifiers);
        _ = IlasmLocator.Assemble(session.ToIlAsm());
    }

    /// <summary>
    /// Loaded method and field operands retain metadata order in live, listed, and exported bodies.
    /// </summary>
    [TestMethod]
    public void LoadedOperands_InterleavedModifiers_PreserveTheirOrderEverywhere()
    {
        var session = new Session();
        var (_, _, fixture) = CecilFixture.Build((module, type) =>
        {
            var method = new MethodDefinition(
                "Echo",
                MethodAttributes.Public | MethodAttributes.Static,
                Annotated(module, parameter: false));
            method.Parameters.Add(new ParameterDefinition("value", ParameterAttributes.None, Annotated(module, parameter: true)));
            var processor = method.Body.GetILProcessor();
            processor.Emit(OpCodes.Ldarg_0);
            processor.Emit(OpCodes.Ret);
            type.Methods.Add(method);
            type.Fields.Add(new FieldDefinition(
                "Value",
                FieldAttributes.Public | FieldAttributes.Static,
                Annotated(module, parameter: false)));
        }, session.Resolver, "Modifiers");
        var owner = $"[{fixture.Assembly.GetName().Name}]N.Modifiers";
        session.AddLine("ldc.i4 7");
        session.AddLine($"call {ReturnType} {owner}::Echo({ParameterType})");
        session.AddLine($"stsfld {ReturnType} {owner}::Value");
        session.AddLine($"ldsfld {ReturnType} {owner}::Value");

        var il = session.ToIlAsm();
        Assert.Contains($"call {ReturnType} {owner}::Echo({ParameterType})", il);
        Assert.Contains($"ldsfld {ReturnType} {owner}::Value", il);
        _ = IlasmLocator.Assemble(il);

        using var exported = AssemblyDefinition.ReadAssembly(new MemoryStream(AssemblyExporter.Write(session, "loaded-modifiers")));
        var run = exported.MainModule.GetType("IlRepl.Cell").Methods.Single(method => method.Name == "Run");
        var called = (MethodReference)run.Body.Instructions.Single(instruction => instruction.OpCode == OpCodes.Call).Operand;
        AssertModifiers(called.ReturnType, ReturnModifiers);
        AssertModifiers(called.Parameters.Single().ParameterType, ParameterModifiers);
        var field = (FieldReference)run.Body.Instructions.Single(instruction => instruction.OpCode == OpCodes.Ldsfld).Operand;
        AssertModifiers(field.FieldType, ReturnModifiers);
        Assert.AreEqual(7, session.Run().Value);
    }

    /// <summary>
    /// Loaded constructor operands retain the custom modifiers attached to their metadata return.
    /// </summary>
    [TestMethod]
    public void LoadedConstructor_AnnotatedReturn_RoundTripsExactly()
    {
        var session = new Session();
        var (_, _, fixture) = CecilFixture.Build((module, type) =>
        {
            var constructor = new MethodDefinition(
                ".ctor",
                MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
                Annotated(module, parameter: false, module.TypeSystem.Void));
            var processor = constructor.Body.GetILProcessor();
            processor.Emit(OpCodes.Ldarg_0);
            processor.Emit(OpCodes.Call, module.ImportReference(typeof(object).GetConstructor(Type.EmptyTypes)!));
            processor.Emit(OpCodes.Ret);
            type.Methods.Add(constructor);
        }, session.Resolver, "Constructors");
        var owner = $"[{fixture.Assembly.GetName().Name}]N.Constructors";
        var returnType = $"void modreq({Volatile}) modopt({Long}) modreq({Cdecl})";

        session.AddLine($"newobj instance {returnType} {owner}::.ctor()");
        session.AddLine("pop");
        session.AddLine("ldc.i4 7");

        var constructor = fixture.GetConstructor(Type.EmptyTypes)!;
        Assert.IsTrue(CecilMetadataSignatures.IsRequired(constructor));
        Assert.AreEqual(
            "void modreq(IsVolatile) modopt(IsLong) modreq(CallConvCdecl)",
            SymbolRenderer.Annotated(RuntimeSymbolImporter.Import(constructor).ExactReturnType!));
        Assert.Contains($"newobj instance {returnType} {owner}::.ctor()", session.ToIlAsm());
        _ = IlasmLocator.Assemble(session.ToIlAsm());

        using var exported = AssemblyDefinition.ReadAssembly(new MemoryStream(AssemblyExporter.Write(session, "constructor-modifiers")));
        var run = exported.MainModule.GetType("IlRepl.Cell").Methods.Single(method => method.Name == "Run");
        var created = (MethodReference)run.Body.Instructions.Single(instruction => instruction.OpCode == OpCodes.Newobj).Operand;
        AssertModifiers(created.ReturnType, ReturnModifiers);
        Assert.AreEqual(7, session.Run().Value);
    }

    /// <summary>
    /// Loaded method overloads differing by custom modifiers bind to the exact requested signature.
    /// </summary>
    [TestMethod]
    public void LoadedMethodOverloads_CustomModifiers_SelectTheExactSignature()
    {
        var session = new Session();
        var owner = BuildModifierOverloads(session, includeConstructors: false);

        session.AddLine("ldc.i4.0");
        session.AddLine($"call int32 {owner}::F(int32 modreq({Volatile}))");
        Assert.AreEqual(1, session.Run().Value);

        session.ClearCell();
        session.AddLine("ldc.i4.0");
        session.AddLine($"call int32 {owner}::F(int32 modopt({Volatile}))");
        Assert.AreEqual(2, session.Run().Value);
    }

    /// <summary>
    /// An unmatched annotated method reference reports each candidate's exact custom modifiers.
    /// </summary>
    [TestMethod]
    public void LoadedMethodOverloads_UnmatchedModifier_ReportsExactCandidates()
    {
        var session = new Session();
        var owner = BuildModifierOverloads(session, includeConstructors: false);

        session.AddLine("ldc.i4.0");
        var error = Assert.ThrowsExactly<ReplException>(() =>
            session.AddLine($"call int32 {owner}::F(int32 modreq({Cdecl}))"));

        Assert.Contains("F(int32 modreq(CallConvCdecl))", error.Message);
        Assert.Contains("int32 modreq(IsVolatile)", error.Message);
        Assert.Contains("int32 modopt(IsVolatile)", error.Message);
    }

    /// <summary>
    /// Loaded constructor overloads differing by custom modifiers bind to the exact requested signature.
    /// </summary>
    [TestMethod]
    public void LoadedConstructorOverloads_CustomModifiers_SelectTheExactSignature()
    {
        var session = new Session();
        var owner = BuildModifierOverloads(session, includeConstructors: true);

        session.AddLine("ldc.i4.0");
        session.AddLine($"newobj instance void {owner}::.ctor(int32 modreq({Volatile}))");
        session.AddLine($"ldfld int32 {owner}::Value");
        Assert.AreEqual(1, session.Run().Value);

        session.ClearCell();
        session.AddLine("ldc.i4.0");
        session.AddLine($"newobj instance void {owner}::.ctor(int32 modopt({Volatile}))");
        session.AddLine($"ldfld int32 {owner}::Value");
        Assert.AreEqual(2, session.Run().Value);
    }

    /// <summary>
    /// Return modifiers participate in loaded overload selection when the reference writes a return type.
    /// </summary>
    [TestMethod]
    public void LoadedMethodOverloads_ReturnModifiers_SelectTheExactSignature()
    {
        var session = new Session();
        var owner = BuildModifierOverloads(session, includeConstructors: false);

        session.AddLine($"call int32 modreq({Volatile}) {owner}::R()");
        Assert.AreEqual(3, session.Run().Value);

        session.ClearCell();
        session.AddLine($"call int32 modopt({Volatile}) {owner}::R()");
        Assert.AreEqual(4, session.Run().Value);
    }

    /// <summary>
    /// Own and inherited member lookup distinguish overloads by their exact parameter annotations.
    /// </summary>
    [TestMethod]
    public void DeclaredMethodOverloads_CustomModifiers_SelectTheExactSignature()
    {
        var session = IlLines.Load(
            ".class public Base {",
            ".method public instance void .ctor() { ldarg.0; call instance void [System.Runtime]System.Object::.ctor(); ret }",
            $".method public instance int32 F(int32 modreq({Volatile}) value) {{ ldc.i4 1; ret }}",
            $".method public instance int32 F(int32 modopt({Volatile}) value) {{ ldc.i4 2; ret }}",
            ".method public instance int32 PickOwn() {",
            "ldarg.0",
            "ldc.i4.0",
            $"call instance int32 Base::F(int32 modreq({Volatile}))",
            "ret",
            "}",
            "}",
            ".class public Derived extends Base {",
            ".method public instance void .ctor() { ldarg.0; call instance void Base::.ctor(); ret }",
            ".method public instance int32 Pick() {",
            "ldarg.0",
            "ldc.i4.0",
            $"call instance int32 Derived::F(int32 modopt({Volatile}))",
            "ret",
            "}",
            "}");

        session.AddLine("newobj instance void Base::.ctor()");
        session.AddLine("call instance int32 Base::PickOwn()");
        Assert.AreEqual(1, session.Run().Value);

        session.ClearCell();
        session.AddLine("newobj instance void Derived::.ctor()");
        session.AddLine("call instance int32 Derived::Pick()");
        Assert.AreEqual(2, session.Run().Value);
    }

    /// <summary>
    /// Generic method-definition references use exact annotations when choosing their metadata token.
    /// </summary>
    [TestMethod]
    public void LoadedGenericDefinitions_CustomModifiers_SelectTheExactSignature()
    {
        var session = new Session();
        var owner = BuildModifierOverloads(session, includeConstructors: false);

        session.AddLine($"ldtoken method int32 {owner}::G<[1]>(!!0 modreq({Volatile}))");
        session.AddLine("pop");
        session.AddLine($"ldtoken method int32 {owner}::G<[1]>(!!0 modopt({Volatile}))");
        session.AddLine("pop");
        session.AddLine("ldc.i4.1");
        Assert.AreEqual(1, session.Run().Value);

        session.ClearCell();
        session.AddLine("ldc.i4.0");
        session.AddLine($"call int32 {owner}::G<int32>(!!0 modreq({Volatile}))");
        Assert.AreEqual(5, session.Run().Value);

        session.ClearCell();
        session.AddLine("ldc.i4.0");
        session.AddLine($"call int32 {owner}::G<int32>(!!0 modopt({Volatile}))");
        Assert.AreEqual(6, session.Run().Value);
    }

    /// <summary>
    /// Vararg call sites retain the complete optional parameter type after their sentinel.
    /// </summary>
    [TestMethod]
    public void VarargCallSite_AnnotatedOptionalParameter_RoundTripsExactly()
    {
        var session = new Session();
        var (_, _, fixture) = CecilFixture.Build((module, type) =>
        {
            var count = new MethodDefinition("Count", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Int32)
            {
                CallingConvention = MethodCallingConvention.VarArg,
            };
            var processor = count.Body.GetILProcessor();
            processor.Emit(OpCodes.Ldc_I4_1);
            processor.Emit(OpCodes.Ret);
            type.Methods.Add(count);
        }, session.Resolver, "Varargs");
        var owner = $"[{fixture.Assembly.GetName().Name}]N.Varargs";

        session.AddLine("ldc.i4 7");
        session.AddLine($"call vararg int32 {owner}::Count(..., {ParameterType})");

        Assert.Contains($"call vararg int32 {owner}::Count(..., {ParameterType})", session.ToIlAsm());
        _ = IlasmLocator.Assemble(session.ToIlAsm());

        using var exported = AssemblyDefinition.ReadAssembly(new MemoryStream(AssemblyExporter.Write(session, "vararg-modifiers")));
        var run = exported.MainModule.GetType("IlRepl.Cell").Methods.Single(method => method.Name == "Run");
        var called = (MethodReference)run.Body.Instructions.Single(instruction => instruction.OpCode == OpCodes.Call).Operand;
        var sentinel = (SentinelType)called.Parameters.Single().ParameterType;
        AssertModifiers(sentinel.ElementType, ParameterModifiers);
    }

    /// <summary>
    /// Property returns and index parameters retain interleaved modifiers in accessors and metadata.
    /// </summary>
    [TestMethod]
    public void Property_InterleavedModifiers_PreservesItsCompleteSignature()
    {
        var session = IlLines.Load(
            ".class public Properties {",
            $".method public specialname static {ReturnType} get_Item({ParameterType} value) {{ ldarg value; ret }}",
            $".property {ReturnType} Item({ParameterType}) {{",
            $".get {ReturnType} Properties::get_Item({ParameterType})",
            "}",
            "}");

        AssertProperty(session.Types.Single().Definition!.Image!, "Properties", "Item");
        Assert.AreEqual(
            $"static property {PrettyReturnType} Item({PrettyParameterType}) {{ get }}",
            session.Types.Single().Declaration.Properties.Single().Describe());
        var il = session.ToIlAsm();
        Assert.Contains($".property {ReturnType} Item({ParameterType})", il);
        Assert.Contains($".get {ReturnType} Properties::get_Item({ParameterType})", il);
        _ = IlasmLocator.Assemble(il);

        var property = session.Types.Single().RuntimeType!.GetProperty("Item")!;
        Assert.AreEqual(7, property.GetValue(null, [7]));
        var exported = AssemblyExporter.Write(session, "property-modifiers");
        AssertProperty(exported, "Properties", "Item");
    }

    /// <summary>
    /// Replacing a modifier type rebuilds property and accessor signatures against its new identity.
    /// </summary>
    [TestMethod]
    public void Property_ModifierTypeReplacement_RebuildsItsCompleteSignature()
    {
        var exact = $"int32 modreq({Volatile}) modopt(Marker) modreq({Long})";
        var session = IlLines.Load(
            ".class public Marker { }",
            ".class public Properties {",
            $".method public specialname static {exact} get_Value() {{ ldc.i4 7; ret }}",
            $".property {exact} Value() {{",
            $".get {exact} Properties::get_Value()",
            "}",
            "}");
        var previousProperties = session.Types.Single(type => type.Declaration.FullName == "Properties").RuntimeType;

        session.AddLine(".class public Marker {");
        session.AddLine(".field public int32 Value");
        var result = session.AddLine("}");

        Assert.Contains("rebuilt class Properties", result.Message!);
        var marker = session.Types.Single(type => type.Declaration.FullName == "Marker").RuntimeType!;
        var properties = session.Types.Single(type => type.Declaration.FullName == "Properties");
        Assert.AreNotSame(previousProperties, properties.RuntimeType);
        Assert.AreSame(marker, properties.RuntimeType!.GetProperty("Value")!.GetOptionalCustomModifiers().Single());
        AssertModifiers(
            Property(properties.Definition!.Image!, "Properties", "Value").PropertyType,
            ["modreq:IsVolatile", "modopt:Marker", "modreq:IsLong"]);
        _ = AssemblyExporter.Write(session, "property-modifier-replacement");
    }

    /// <summary>
    /// Cell arguments and locals retain interleaved modifiers while running, listing, and exporting.
    /// </summary>
    [TestMethod]
    public void CellSlots_InterleavedModifiers_PreserveTheirOrderEverywhere()
    {
        var lines = new[]
        {
            $".args ({ParameterType} source = 7)",
            $".locals init ({ReturnType} scratch)",
            "ldarg source",
            "stloc scratch",
            "ldloc scratch",
        };
        var session = IlLines.Load(lines);

        using var editing = new EditingSession(new Session());
        var view = editing.Speculate([.. lines, ""], lines.Length,
            cancellationToken: TestContext.CancellationToken);
        Assert.IsEmpty(view.SkippedLines, string.Join("; ", view.SkippedLines.Select(line => line.Message)));
        Assert.IsTrue(SymbolIdentity.Equal(TypeSymbol.Primitive("int32"), view.Stack.Single()));

        var il = session.ToIlAsm();
        Assert.Contains($"object Run({ParameterType} source)", il);
        Assert.Contains($".locals init ([0] {ReturnType} scratch)", il);
        _ = IlasmLocator.Assemble(il);

        using var exported = AssemblyDefinition.ReadAssembly(new MemoryStream(AssemblyExporter.Write(session, "slot-modifiers")));
        var run = exported.MainModule.GetType("IlRepl.Cell").Methods.Single(method => method.Name == "Run");
        AssertModifiers(run.Parameters.Single().ParameterType, ParameterModifiers);
        AssertModifiers(run.Body.Variables.Single().VariableType, ReturnModifiers);
        Assert.AreEqual(7, session.Run().Value);
    }

    /// <summary>
    /// Public symbol copies rebase exact modifier chains onto their substituted generic core types.
    /// </summary>
    [TestMethod]
    public void SymbolCopies_AnnotatedGenericTypes_RebaseTheirExactType()
    {
        var parameter = TypeSymbol.Parameter(
            DefinitionId.None, false, 0, "T", GenericParameterAttributes.None);
        var modifier = RuntimeSymbolImporter.Import(typeof(IsVolatile));
        var exact = TypeSymbol.Modified(parameter, modifier, true);
        var replacement = TypeSymbol.Primitive("int32");
        var method = new MethodSymbol
        {
            Definition = DefinitionId.None,
            Source = MethodSymbolSource.Loaded,
            Name = "F",
            ReturnType = parameter,
            ExactReturnType = exact,
            Parameters = [new ParameterSymbol(parameter, "value") { ExactType = exact }],
        };
        var field = new FieldSymbol
        {
            Definition = DefinitionId.None,
            Source = MethodSymbolSource.Loaded,
            DeclaringType = TypeSymbol.Object,
            Name = "F",
            FieldType = parameter,
            ExactType = exact,
        };

        var copiedMethod = method.With(
            null,
            replacement,
            [new ParameterSymbol(replacement, "value")],
            []);
        var copiedField = field.With(TypeSymbol.Object, replacement);

        Assert.IsTrue(SymbolIdentity.Equal(replacement, copiedMethod.ExactReturnType!.Element));
        Assert.IsTrue(SymbolIdentity.Equal(replacement, copiedMethod.Parameters.Single().ExactType!.Element));
        Assert.IsTrue(SymbolIdentity.Equal(replacement, copiedField.ExactType!.Element));
        Assert.IsTrue(SymbolIdentity.Equal(modifier, copiedMethod.ExactReturnType.Modifier));
        Assert.IsTrue(SymbolIdentity.Equal(modifier, copiedMethod.Parameters.Single().ExactType!.Modifier));
        Assert.IsTrue(SymbolIdentity.Equal(modifier, copiedField.ExactType.Modifier));
    }

    /// <summary>
    /// Signature identity observes joint modifier order only when both signatures retain it.
    /// </summary>
    [TestMethod]
    public void SignatureIdentity_ExactAndFallbackAnnotations_CompareWithoutInventingOrder()
    {
        var int32 = TypeSymbol.Primitive("int32");
        var required = RuntimeSymbolImporter.Import(typeof(IsVolatile));
        var optional = RuntimeSymbolImporter.Import(typeof(IsLong));
        var requiredThenOptional = TypeSymbol.Modified(TypeSymbol.Modified(int32, required, true), optional, false);
        var optionalThenRequired = TypeSymbol.Modified(TypeSymbol.Modified(int32, optional, false), required, true);
        var first = AnnotatedMethod(requiredThenOptional, required, optional);
        var reordered = AnnotatedMethod(optionalThenRequired, required, optional);
        var fallback = AnnotatedMethod(null, required, optional);

        Assert.IsFalse(SignatureSymbolIdentity.Equal(first, reordered));
        Assert.IsTrue(SignatureSymbolIdentity.Equal(first, fallback));
    }

    private static void AssertMethod(byte[] image, string typeName, string methodName)
    {
        var method = Method(image, typeName, methodName);
        AssertModifiers(method.ReturnType, ReturnModifiers);
        AssertModifiers(method.Parameters.Single().ParameterType, ParameterModifiers);
    }

    private static MethodSymbol AnnotatedMethod(TypeSymbol? exactReturnType, TypeSymbol required, TypeSymbol optional) => new()
    {
        Definition = DefinitionId.None,
        Source = MethodSymbolSource.Loaded,
        Name = "F",
        ReturnType = TypeSymbol.Primitive("int32"),
        ExactReturnType = exactReturnType,
        ReturnRequiredModifiers = exactReturnType is null ? [required] : [],
        ReturnOptionalModifiers = exactReturnType is null ? [optional] : [],
    };

    private static string BuildModifierOverloads(Session session, bool includeConstructors)
    {
        var (_, _, fixture) = CecilFixture.Build((module, type) =>
        {
            var volatileType = module.ImportReference(typeof(IsVolatile));
            AddMethod(type, "F", new RequiredModifierType(volatileType, module.TypeSystem.Int32), 1);
            AddMethod(type, "F", new OptionalModifierType(volatileType, module.TypeSystem.Int32), 2);
            AddReturnMethod(type, "R", new RequiredModifierType(volatileType, module.TypeSystem.Int32), 3);
            AddReturnMethod(type, "R", new OptionalModifierType(volatileType, module.TypeSystem.Int32), 4);
            AddGenericMethod(type, volatileType, required: true, 5);
            AddGenericMethod(type, volatileType, required: false, 6);
            if (includeConstructors)
            {
                var field = new FieldDefinition("Value", FieldAttributes.Public, module.TypeSystem.Int32);
                type.Fields.Add(field);
                AddConstructor(module, type, field, new RequiredModifierType(volatileType, module.TypeSystem.Int32), 1);
                AddConstructor(module, type, field, new OptionalModifierType(volatileType, module.TypeSystem.Int32), 2);
            }
        }, session.Resolver, "ModifierOverloads");
        return $"[{fixture.Assembly.GetName().Name}]N.ModifierOverloads";
    }

    private static void AddMethod(TypeDefinition type, string name, TypeReference parameterType, int value)
    {
        var method = new MethodDefinition(name, MethodAttributes.Public | MethodAttributes.Static, type.Module.TypeSystem.Int32);
        method.Parameters.Add(new ParameterDefinition("value", ParameterAttributes.None, parameterType));
        var processor = method.Body.GetILProcessor();
        processor.Emit(OpCodes.Ldc_I4, value);
        processor.Emit(OpCodes.Ret);
        type.Methods.Add(method);
    }

    private static void AddReturnMethod(TypeDefinition type, string name, TypeReference returnType, int value)
    {
        var method = new MethodDefinition(name, MethodAttributes.Public | MethodAttributes.Static, returnType);
        var processor = method.Body.GetILProcessor();
        processor.Emit(OpCodes.Ldc_I4, value);
        processor.Emit(OpCodes.Ret);
        type.Methods.Add(method);
    }

    private static void AddGenericMethod(TypeDefinition type, TypeReference modifier, bool required, int value)
    {
        var method = new MethodDefinition("G", MethodAttributes.Public | MethodAttributes.Static, type.Module.TypeSystem.Int32);
        var parameter = new GenericParameter("T", method);
        method.GenericParameters.Add(parameter);
        var parameterType = required
            ? (TypeReference)new RequiredModifierType(modifier, parameter)
            : new OptionalModifierType(modifier, parameter);
        method.Parameters.Add(new ParameterDefinition("value", ParameterAttributes.None, parameterType));
        var processor = method.Body.GetILProcessor();
        processor.Emit(OpCodes.Ldc_I4, value);
        processor.Emit(OpCodes.Ret);
        type.Methods.Add(method);
    }

    private static void AddConstructor(
        ModuleDefinition module,
        TypeDefinition type,
        FieldReference field,
        TypeReference parameterType,
        int value)
    {
        var constructor = new MethodDefinition(
            ".ctor",
            MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
            module.TypeSystem.Void);
        constructor.Parameters.Add(new ParameterDefinition("value", ParameterAttributes.None, parameterType));
        var processor = constructor.Body.GetILProcessor();
        processor.Emit(OpCodes.Ldarg_0);
        processor.Emit(OpCodes.Call, module.ImportReference(typeof(object).GetConstructor(Type.EmptyTypes)!));
        processor.Emit(OpCodes.Ldarg_0);
        processor.Emit(OpCodes.Ldc_I4, value);
        processor.Emit(OpCodes.Stfld, field);
        processor.Emit(OpCodes.Ret);
        type.Methods.Add(constructor);
    }

    private static TypeReference Annotated(ModuleDefinition module, bool parameter, TypeReference? elementType = null)
    {
        (Type Type, bool Required)[] modifiers = parameter
            ? [(typeof(CallConvCdecl), false), (typeof(IsVolatile), true), (typeof(IsLong), false)]
            : [(typeof(IsVolatile), true), (typeof(IsLong), false), (typeof(CallConvCdecl), true)];
        var result = elementType ?? module.TypeSystem.Int32;
        foreach (var modifier in modifiers)
        {
            var imported = module.ImportReference(modifier.Type);
            result = modifier.Required
                ? new RequiredModifierType(imported, result)
                : new OptionalModifierType(imported, result);
        }

        return result;
    }

    private static MethodDefinition Method(byte[] image, string typeName, string methodName)
    {
        using var definition = AssemblyDefinition.ReadAssembly(new MemoryStream(image));
        var slash = typeName.IndexOf('/', StringComparison.Ordinal);
        var type = slash < 0
            ? definition.MainModule.GetType(typeName)
            : definition.MainModule.GetType(typeName[..slash]).NestedTypes.Single(
                nested => nested.Name == typeName[(slash + 1)..]);
        return type.Methods.Single(method => method.Name == methodName);
    }

    private static TypeReference MethodType(byte[] image, string typeName, string fieldName)
    {
        using var definition = AssemblyDefinition.ReadAssembly(new MemoryStream(image));
        return definition.MainModule.GetType(typeName).Fields.Single(field => field.Name == fieldName).FieldType;
    }

    private static void AssertProperty(byte[] image, string typeName, string propertyName)
    {
        var property = Property(image, typeName, propertyName);
        AssertModifiers(property.PropertyType, ReturnModifiers);
        AssertModifiers(property.Parameters.Single().ParameterType, ParameterModifiers);
    }

    private static PropertyDefinition Property(byte[] image, string typeName, string propertyName)
    {
        using var definition = AssemblyDefinition.ReadAssembly(new MemoryStream(image));
        return definition.MainModule.GetType(typeName).Properties.Single(candidate => candidate.Name == propertyName);
    }

    private static void AssertModifiers(TypeReference type, IReadOnlyList<string> expected)
    {
        var actual = new List<string>();
        Read(type, actual);
        Assert.AreSequenceEqual(expected, actual);
    }

    private static void Read(TypeReference type, ICollection<string> modifiers)
    {
        switch (type)
        {
            case RequiredModifierType required:
                Read(required.ElementType, modifiers);
                modifiers.Add("modreq:" + required.ModifierType.Name);
                break;
            case OptionalModifierType optional:
                Read(optional.ElementType, modifiers);
                modifiers.Add("modopt:" + optional.ModifierType.Name);
                break;
        }
    }
}
