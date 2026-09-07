using System.Reflection;
using System.Reflection.Metadata;
using IlRepl.Engine;
using Mono.Cecil;
using Mono.Cecil.Cil;
using FieldDefinition = Mono.Cecil.FieldDefinition;
using GenericParameter = Mono.Cecil.GenericParameter;
using MethodDefinition = Mono.Cecil.MethodDefinition;
using TypeReference = Mono.Cecil.TypeReference;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Tests for <see cref="MetadataSignatures"/>, <see cref="MetadataSignatureProvider"/>, and
/// <see cref="IlSignatureRenderer"/>: signatures come out of the module's metadata whole.
/// </summary>
[TestClass]
public sealed class MetadataSignaturesTests
{
    private static (MetadataReader Reader, MetadataSignatureProvider Provider) Open(Module module)
    {
        var reader = ModuleMetadata.TryOpen(module);
        Assert.IsNotNull(reader, "the runtime exposes the metadata section of a loaded assembly");
        return (reader, new MetadataSignatureProvider(token =>
        {
            try
            {
                return module.ResolveType(token);
            }
            catch (Exception ex) when (ex is ArgumentException or FileNotFoundException or FileLoadException or TypeLoadException or BadImageFormatException)
            {
                return null;
            }
        }));
    }

    private static IlSignature Field(Type fixture, string name)
    {
        var field = fixture.GetField(name, BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)!;
        var (reader, provider) = Open(fixture.Module);
        var context = new GenericContext(fixture.IsGenericTypeDefinition ? fixture.GetGenericArguments() : [], []);
        var signature = MetadataSignatures.FieldOperand(reader, field.MetadataToken, provider, context);
        Assert.IsNotNull(signature);
        return signature;
    }

    /// <summary>
    /// A custom modifier stays on the field type, in position.
    /// </summary>
    [TestMethod]
    public void Field_VolatileModifier_IsKept()
    {
        var (_, _, fixture) = CecilFixture.Build((module, type) =>
            type.Fields.Add(new FieldDefinition("Data", Mono.Cecil.FieldAttributes.Public | Mono.Cecil.FieldAttributes.Static,
                new RequiredModifierType(module.ImportReference(typeof(System.Runtime.CompilerServices.IsVolatile)), module.TypeSystem.Int32))));
        var signature = Field(fixture, "Data");
        Assert.AreEqual(IlSignatureKind.Modified, signature.Kind);
        Assert.IsTrue(signature.IsRequired);
        Assert.AreEqual("int32 modreq([System.Runtime]System.Runtime.CompilerServices.IsVolatile)", IlSignatureRenderer.IlAsm(signature));
        Assert.AreEqual("int32 modreq(IsVolatile)", IlSignatureRenderer.Pretty(signature));
        Assert.AreEqual(typeof(int), signature.ToClrType());
    }

    /// <summary>
    /// An array keeps its sizes and lower bounds, which reflection drops.
    /// </summary>
    [TestMethod]
    public void Field_ArrayShape_IsKept()
    {
        var (_, _, fixture) = CecilFixture.Build((module, type) =>
        {
            var shaped = new ArrayType(module.TypeSystem.Int32, 2);
            shaped.Dimensions[0] = new ArrayDimension(1, 3);
            shaped.Dimensions[1] = new ArrayDimension(0, null);
            type.Fields.Add(new FieldDefinition("Grid", Mono.Cecil.FieldAttributes.Public, shaped));
            // Cecil writes an upper bound as a lower bound of 0 and a size, which ildasm prints as 0...3.
            var sized = new ArrayType(module.TypeSystem.Int32, 1);
            sized.Dimensions[0] = new ArrayDimension(null, 3);
            type.Fields.Add(new FieldDefinition("Three", Mono.Cecil.FieldAttributes.Public, sized));
            type.Fields.Add(new FieldDefinition("Vector", Mono.Cecil.FieldAttributes.Public, new ArrayType(module.TypeSystem.String)));
        });
        Assert.AreEqual("int32[1...3,0...]", IlSignatureRenderer.IlAsm(Field(fixture, "Grid")));
        Assert.AreEqual(typeof(int[,]), Field(fixture, "Grid").ToClrType());
        Assert.AreEqual("int32[0...3]", IlSignatureRenderer.IlAsm(Field(fixture, "Three")));
        Assert.AreEqual("string[]", IlSignatureRenderer.IlAsm(Field(fixture, "Vector")));
        Assert.AreEqual(typeof(string[]), Field(fixture, "Vector").ToClrType());
    }

    /// <summary>
    /// A function pointer field renders its whole signature and projects to native int.
    /// </summary>
    [TestMethod]
    public void Field_FunctionPointer_RendersSignature()
    {
        var (_, _, fixture) = CecilFixture.Build((module, type) =>
        {
            var unmanaged = new FunctionPointerType { ReturnType = module.TypeSystem.Int32, CallingConvention = MethodCallingConvention.C };
            unmanaged.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int32));
            type.Fields.Add(new FieldDefinition("Native", Mono.Cecil.FieldAttributes.Public, unmanaged));
            var managed = new FunctionPointerType { ReturnType = module.TypeSystem.Void, HasThis = true };
            managed.Parameters.Add(new ParameterDefinition(new ArrayType(unmanaged)));
            type.Fields.Add(new FieldDefinition("Managed", Mono.Cecil.FieldAttributes.Public, managed));
        });
        Assert.AreEqual("method unmanaged cdecl int32 *(int32)", IlSignatureRenderer.IlAsm(Field(fixture, "Native")));
        Assert.AreEqual(typeof(nint), Field(fixture, "Native").ToClrType());
        Assert.AreEqual("method instance void *(method unmanaged cdecl int32 *(int32)[])", IlSignatureRenderer.IlAsm(Field(fixture, "Managed")));

        // The reflection path spells a function pointer type too, though it cannot recover the
        // specific convention from the legacy convention byte, which is one reason the listing
        // renders from metadata.
        var reflected = fixture.GetField("Native")!.FieldType;
        Assert.IsTrue(reflected.IsFunctionPointer);
        Assert.AreEqual("method unmanaged int32 *(int32)", TypeNameFormatter.IlAsm(reflected));
        Assert.AreEqual("method unmanaged int32 *(int32)", TypeNameFormatter.Pretty(reflected));
    }

    /// <summary>
    /// Generic parameters render by position, or by name when the context knows them, and generic instances keep their arguments.
    /// </summary>
    [TestMethod]
    public void Field_GenericParameterAndInstance_Render()
    {
        var (_, _, fixture) = CecilFixture.Build((module, type) =>
        {
            var t = new GenericParameter("T", type);
            type.GenericParameters.Add(t);
            type.Fields.Add(new FieldDefinition("Item", Mono.Cecil.FieldAttributes.Public, t));
            var list = new GenericInstanceType(module.ImportReference(typeof(List<>)));
            list.GenericArguments.Add(t);
            type.Fields.Add(new FieldDefinition("Items", Mono.Cecil.FieldAttributes.Public, list));
        });
        var item = Field(fixture, "Item");
        Assert.AreEqual("!0", IlSignatureRenderer.IlAsm(item));
        Assert.AreEqual("!T", IlSignatureRenderer.IlAsmNamed(item));
        Assert.AreEqual(fixture.GetGenericArguments()[0], item.ToClrType());
        var items = Field(fixture, "Items");
        Assert.AreEqual(TypeNameFormatter.IlAsmDefinition(typeof(List<>)) + "<!0>", IlSignatureRenderer.IlAsm(items));
        Assert.AreEqual("List<!T>", IlSignatureRenderer.Pretty(items));
        Assert.AreEqual(typeof(List<>).MakeGenericType(fixture.GetGenericArguments()[0]), items.ToClrType());
    }

    /// <summary>
    /// A type that does not resolve keeps the spelling of its row and projects to an unknown slot.
    /// </summary>
    [TestMethod]
    public void Field_UnresolvedReference_KeepsRowSpelling()
    {
        var (_, _, fixture) = CecilFixture.Build((module, type) =>
        {
            var scope = new AssemblyNameReference("Nowhere", new Version(1, 0, 0, 0));
            module.AssemblyReferences.Add(scope);
            var missing = new TypeReference("Missing", "Thing", module, scope) { IsValueType = true };
            type.Fields.Add(new FieldDefinition("Gone", Mono.Cecil.FieldAttributes.Public, new ArrayType(missing)));
        });
        var gone = Field(fixture, "Gone");
        Assert.IsTrue(gone.HasUnresolved);
        Assert.AreEqual("valuetype [Nowhere]Missing.Thing[]", IlSignatureRenderer.IlAsm(gone));
        Assert.AreEqual("Thing[]", IlSignatureRenderer.Pretty(gone));
        Assert.IsNull(gone.ToClrType());
    }

    /// <summary>
    /// Locals decode from the body's signature token with pinning and the init flag intact.
    /// </summary>
    [TestMethod]
    public void Locals_PinnedAndUninitialized_Decode()
    {
        var (_, _, fixture) = CecilFixture.Build((module, type) =>
        {
            var method = new MethodDefinition("M", Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Static, module.TypeSystem.Void);
            method.Body.InitLocals = false;
            method.Body.Variables.Add(new VariableDefinition(new PinnedType(new ByReferenceType(module.TypeSystem.Int32))));
            method.Body.Variables.Add(new VariableDefinition(new PointerType(module.TypeSystem.Byte)));
            method.Body.GetILProcessor().Emit(OpCodes.Ret);
            type.Methods.Add(method);
        });
        var method = fixture.GetMethod("M")!;
        var body = method.GetMethodBody()!;
        Assert.IsFalse(body.InitLocals);
        var (reader, provider) = Open(fixture.Module);
        var locals = MetadataSignatures.Locals(reader, body.LocalSignatureMetadataToken, provider, GenericContext.Empty);
        Assert.IsNotNull(locals);
        Assert.AreSequenceEqual(["int32& pinned", "uint8*"], locals.Select(IlSignatureRenderer.IlAsm).ToList());
        Assert.AreEqual(typeof(int).MakeByRefType(), locals[0].ToClrType());
        Assert.IsEmpty(MetadataSignatures.Locals(reader, 0, provider, GenericContext.Empty)!);
    }

    /// <summary>
    /// A calli signature keeps its convention bits, and an explicit this is counted once.
    /// </summary>
    [TestMethod]
    public void Calli_ExplicitThisAndUnmanaged_Decode()
    {
        var (_, _, fixture) = CecilFixture.Build((module, type) =>
        {
            var method = new MethodDefinition("M", Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Static, module.TypeSystem.Void);
            var il = method.Body.GetILProcessor();
            var explicitThis = new CallSite(module.TypeSystem.Int32) { HasThis = true, ExplicitThis = true };
            explicitThis.Parameters.Add(new ParameterDefinition(module.TypeSystem.Object));
            il.Emit(OpCodes.Calli, explicitThis);
            var native = new CallSite(module.TypeSystem.Void) { CallingConvention = MethodCallingConvention.StdCall };
            native.Parameters.Add(new ParameterDefinition(module.TypeSystem.IntPtr));
            il.Emit(OpCodes.Calli, native);
            var vararg = new CallSite(module.TypeSystem.Int32) { CallingConvention = MethodCallingConvention.VarArg };
            vararg.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
            vararg.Parameters.Add(new ParameterDefinition(new SentinelType(module.TypeSystem.Int32)));
            il.Emit(OpCodes.Calli, vararg);
            il.Emit(OpCodes.Ret);
            type.Methods.Add(method);
        });
        var (reader, provider) = Open(fixture.Module);
        var il = IlReader.Read(fixture.GetMethod("M")!.GetMethodBody()!.GetILAsByteArray()!).Instructions;
        var explicitThis = MetadataSignatures.StandaloneMethod(reader, il[0].Operand.Token, provider, GenericContext.Empty)!;
        Assert.AreEqual("instance explicit int32(object)", IlSignatureRenderer.IlAsm(explicitThis));
        Assert.AreEqual(1, explicitThis.ToCalliSignature()!.ArgumentPopCount);
        var native = MetadataSignatures.StandaloneMethod(reader, il[1].Operand.Token, provider, GenericContext.Empty)!;
        Assert.AreEqual("unmanaged stdcall void(native int)", IlSignatureRenderer.IlAsm(native));
        Assert.IsTrue(native.ToCalliSignature()!.IsUnmanaged);
        var vararg = MetadataSignatures.StandaloneMethod(reader, il[2].Operand.Token, provider, GenericContext.Empty)!;
        Assert.AreEqual("vararg int32(string, ..., int32)", IlSignatureRenderer.IlAsm(vararg));
        Assert.AreEqual(1, vararg.RequiredParameterCount);
        Assert.AreEqual(2, vararg.ToCalliSignature()!.ArgumentPopCount);
    }

    /// <summary>
    /// A vararg call site's MemberRef carries the optional argument types after the sentinel.
    /// </summary>
    [TestMethod]
    public void MethodOperand_VarargMemberRef_HasOptionalTypes()
    {
        var (_, _, fixture) = CecilFixture.Build((module, type) =>
        {
            var callee = new MethodDefinition("Count", Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Static, module.TypeSystem.Int32) { CallingConvention = MethodCallingConvention.VarArg };
            callee.Body.GetILProcessor().Emit(OpCodes.Ldc_I4_0);
            callee.Body.GetILProcessor().Emit(OpCodes.Ret);
            type.Methods.Add(callee);
            var caller = new MethodDefinition("Call", Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Static, module.TypeSystem.Int32);
            var site = new MethodReference("Count", module.TypeSystem.Int32, type) { CallingConvention = MethodCallingConvention.VarArg };
            site.Parameters.Add(new ParameterDefinition(new SentinelType(module.TypeSystem.Int32)));
            site.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
            var il = caller.Body.GetILProcessor();
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Call, site);
            il.Emit(OpCodes.Ret);
            type.Methods.Add(caller);
        });
        var (reader, provider) = Open(fixture.Module);
        var il = IlReader.Read(fixture.GetMethod("Call")!.GetMethodBody()!.GetILAsByteArray()!).Instructions;
        var call = MetadataSignatures.MethodOperand(reader, il[2].Operand.Token, provider, GenericContext.Empty, out var instantiation)!;
        Assert.IsNull(instantiation);
        Assert.IsTrue(call.IsVarArg);
        Assert.AreEqual(0, call.RequiredParameterCount);
        Assert.AreEqual("vararg int32(..., int32, string)", IlSignatureRenderer.IlAsm(call));
        Assert.AreSequenceEqual([typeof(int), typeof(string)], call.OptionalParameters!.Select(p => p.ToClrType()).ToList());
    }

    /// <summary>
    /// A generic method instance operand yields the definition's signature and the instantiation.
    /// </summary>
    [TestMethod]
    public void MethodOperand_MethodSpec_GivesInstantiation()
    {
        var (_, _, fixture) = CecilFixture.Build((module, type) =>
        {
            var caller = new MethodDefinition("Call", Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Static, module.TypeSystem.Void);
            var il = caller.Body.GetILProcessor();
            var empty = new GenericInstanceMethod(module.ImportReference(typeof(Array).GetMethod("Empty")!));
            empty.GenericArguments.Add(module.TypeSystem.String);
            il.Emit(OpCodes.Call, empty);
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ret);
            type.Methods.Add(caller);
        });
        var (reader, provider) = Open(fixture.Module);
        var il = IlReader.Read(fixture.GetMethod("Call")!.GetMethodBody()!.GetILAsByteArray()!).Instructions;
        var call = MetadataSignatures.MethodOperand(reader, il[0].Operand.Token, provider, GenericContext.Empty, out var instantiation)!;
        Assert.AreEqual(1, call.GenericParameterCount);
        Assert.AreEqual("!!0[](", IlSignatureRenderer.IlAsm(call)[..6]);
        Assert.AreSequenceEqual(["string"], instantiation!.Select(IlSignatureRenderer.IlAsm).ToList());
    }

    /// <summary>
    /// Type operands decode from every table a type token can come from.
    /// </summary>
    [TestMethod]
    public void TypeOperand_DefinitionReferenceAndSpecification_Decode()
    {
        var (_, _, fixture) = CecilFixture.Build((module, type) =>
        {
            var method = new MethodDefinition("M", Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Static, module.TypeSystem.Void);
            var il = method.Body.GetILProcessor();
            il.Emit(OpCodes.Ldtoken, type);
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ldtoken, module.ImportReference(typeof(Guid)));
            il.Emit(OpCodes.Pop);
            var pair = new GenericInstanceType(module.ImportReference(typeof(KeyValuePair<,>)));
            pair.GenericArguments.Add(module.TypeSystem.String);
            pair.GenericArguments.Add(new ArrayType(module.TypeSystem.Int32));
            il.Emit(OpCodes.Ldtoken, pair);
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ret);
            type.Methods.Add(method);
        });
        var (reader, provider) = Open(fixture.Module);
        var il = IlReader.Read(fixture.GetMethod("M")!.GetMethodBody()!.GetILAsByteArray()!).Instructions;
        var self = MetadataSignatures.TypeOperand(reader, il[0].Operand.Token, provider, GenericContext.Empty)!;
        Assert.AreEqual(fixture, self.Resolved);
        var guid = MetadataSignatures.TypeOperand(reader, il[2].Operand.Token, provider, GenericContext.Empty)!;
        Assert.AreEqual("valuetype [System.Runtime]System.Guid", IlSignatureRenderer.IlAsm(guid));
        var pair = MetadataSignatures.TypeOperand(reader, il[4].Operand.Token, provider, GenericContext.Empty)!;
        Assert.AreEqual(TypeNameFormatter.IlAsm(typeof(KeyValuePair<string, int[]>)), IlSignatureRenderer.IlAsm(pair));
        Assert.AreEqual(typeof(KeyValuePair<string, int[]>), pair.ToClrType());
    }

    /// <summary>
    /// A method definition's own signature carries its modifiers and generic arity.
    /// </summary>
    [TestMethod]
    public void MethodDefinition_ModifiersAndArity_Decode()
    {
        var (_, _, fixture) = CecilFixture.Build((module, type) =>
        {
            var method = new MethodDefinition("M", Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Static,
                new OptionalModifierType(module.ImportReference(typeof(System.Runtime.CompilerServices.CallConvCdecl)), module.TypeSystem.Void));
            method.GenericParameters.Add(new GenericParameter("U", method));
            method.Parameters.Add(new ParameterDefinition("x", Mono.Cecil.ParameterAttributes.None, new ByReferenceType(method.GenericParameters[0])));
            method.Body.GetILProcessor().Emit(OpCodes.Ret);
            type.Methods.Add(method);
        });
        var method = fixture.GetMethod("M")!;
        var (reader, provider) = Open(fixture.Module);
        var signature = MetadataSignatures.MethodDefinition(reader, method.MetadataToken, provider, new GenericContext([], method.GetGenericArguments()));
        Assert.AreEqual(1, signature.GenericParameterCount);
        Assert.AreEqual("void modopt([System.Runtime]System.Runtime.CompilerServices.CallConvCdecl)", IlSignatureRenderer.IlAsm(signature.ReturnType));
        Assert.AreEqual("!!U&", IlSignatureRenderer.IlAsmNamed(signature.Parameters[0]));
        Assert.AreEqual("!!0&", IlSignatureRenderer.IlAsm(signature.Parameters[0]));
    }
}
