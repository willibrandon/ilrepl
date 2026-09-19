using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using IlRepl.Engine;
using Mono.Cecil;
using CecilMethod = Mono.Cecil.MethodDefinition;
using CecilMethodAttributes = Mono.Cecil.MethodAttributes;
using CecilMethodImplAttributes = Mono.Cecil.MethodImplAttributes;
using CecilModule = Mono.Cecil.ModuleDefinition;
using CecilModuleReference = Mono.Cecil.ModuleReference;
using CecilParameterAttributes = Mono.Cecil.ParameterAttributes;
using CecilProperty = Mono.Cecil.PropertyDefinition;
using CecilPropertyAttributes = Mono.Cecil.PropertyAttributes;
using CecilType = Mono.Cecil.TypeDefinition;
using CecilTypeAttributes = Mono.Cecil.TypeAttributes;
using OpCodes = Mono.Cecil.Cil.OpCodes;
using ReflectionParameterAttributes = System.Reflection.ParameterAttributes;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Imported families preserve complete interop, property, return-default, and explicit base-dispatch metadata.
/// </summary>
[TestClass]
public sealed class ExtendedImportedMetadataTests
{
    /// <summary>
    /// Full SAFEARRAY subtype and IUnknown IID descriptors survive both exported formats byte for byte.
    /// </summary>
    /// <param name="safeArray">Whether to use a SAFEARRAY subtype or an IUnknown IID parameter.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void MarshalDescriptor_PreservesTrailingSubtypeAndIidFields(bool safeArray)
    {
        const string subtype = "System.Runtime.InteropServices.ComTypes.IStream, System.Private.CoreLib";
        var descriptor = safeArray ? "safearray iunknown, \"" + subtype + "\"" : "iunknown(iidparam = 1)";
        var type = safeArray ? "object[]" : "object";
        var source = ".class public N.Fixture extends [System.Runtime]System.Object {\n"
            + ".method public static " + type + " marshal(" + descriptor + ") Echo(" + type + " marshal(" + descriptor
            + ") item, valuetype [System.Runtime]System.Guid& iid) cil managed {\nldarg.0\nret\n}\n}";
        var session = new Session();
        var (assembly, image) = Assemble(session, source);
        var original = assembly.GetType("N.Fixture")!.GetMethod("Echo")!;
        var originalMarshal = original.GetParameters()[0].GetCustomAttribute<MarshalAsAttribute>()!;
        var value = safeArray ? new object[] { "first", 42 } : new object();
        Assert.AreSame(value, original.Invoke(null, [value, Guid.Empty]));
        var expected = new BlobBuilder();
        expected.WriteByte(safeArray ? (byte)0x1d : (byte)0x19);
        expected.WriteCompressedInteger(safeArray ? (int)VarEnum.VT_UNKNOWN : 1);
        if (safeArray)
        {
            expected.WriteSerializedString(subtype);
        }

        AssertDescriptors(image, "N.Fixture", expected.ToArray());
        var edit = Commit(session, type + " [" + assembly.GetName().Name + "]N.Fixture::Echo(" + type
            + ", valuetype [System.Runtime]System.Guid&)");
        Assert.AreSame(value, edit.OriginalMethod.Invoke(null, [value, Guid.Empty]));
        Assert.AreSame(value, edit.Method!.Invoke(null, [value, Guid.Empty]));
        foreach (var exported in Images(session))
        {
            AssertDescriptors(exported, edit.Method.DeclaringType!.FullName!, expected.ToArray());
            InExport(exported, edit, owner =>
            {
                var method = owner.GetMethod("Echo")!;
                Assert.AreSame(value, method.Invoke(null, [value, Guid.Empty]));
                var marshal = method.GetParameters()[0].GetCustomAttribute<MarshalAsAttribute>()!;
                Assert.AreEqual(safeArray ? UnmanagedType.SafeArray : UnmanagedType.IUnknown, marshal.Value);
                if (safeArray)
                {
                    Assert.AreEqual(originalMarshal.SafeArraySubType, marshal.SafeArraySubType);
                    Assert.AreEqual(originalMarshal.SafeArrayUserDefinedSubType, marshal.SafeArrayUserDefinedSubType);
                }
                else
                {
                    Assert.AreEqual(originalMarshal.IidParameterIndex, marshal.IidParameterIndex);
                }
            });
        }
    }

    /// <summary>
    /// A copied private PInvoke helper retains its real OS entry point and works from independently exported images.
    /// </summary>
    [TestMethod]
    public void NativeHelper_PreservesImplMapAndCallsTheActualOperatingSystem()
    {
        var session = new Session();
        var library = OperatingSystem.IsWindows() ? "kernel32.dll"
            : OperatingSystem.IsMacOS() ? "/usr/lib/libSystem.B.dylib" : "libc";
        var entry = OperatingSystem.IsWindows() ? "GetCurrentProcessId" : "getpid";
        var (assembly, _, original) = CecilFixture.Build((module, type) =>
        {
            var nativeModule = new CecilModuleReference(library);
            module.ModuleReferences.Add(nativeModule);
            var native = new CecilMethod("NativeProcessId", CecilMethodAttributes.Private | CecilMethodAttributes.Static
                | CecilMethodAttributes.PInvokeImpl, module.TypeSystem.Int32)
            {
                PInvokeInfo = new PInvokeInfo(PInvokeAttributes.CallConvWinapi | PInvokeAttributes.NoMangle
                    | PInvokeAttributes.SupportsLastError, entry, nativeModule),
                ImplAttributes = CecilMethodImplAttributes.PreserveSig,
            };
            type.Methods.Add(native);
            var read = new CecilMethod("Read", CecilMethodAttributes.Public | CecilMethodAttributes.Static, module.TypeSystem.Int32);
            type.Methods.Add(read);
            read.Body.GetILProcessor().Emit(OpCodes.Call, native);
            read.Body.GetILProcessor().Emit(OpCodes.Ret);
        }, session.Resolver);
        Assert.AreEqual(Environment.ProcessId, original.GetMethod("Read")!.Invoke(null, null));
        var edit = Commit(session, "int32 [" + assembly.GetName().Name + "]N.Fixture::Read()");
        Assert.AreEqual(Environment.ProcessId, edit.OriginalMethod.Invoke(null, null));
        Assert.AreEqual(Environment.ProcessId, edit.Method!.Invoke(null, null));
        foreach (var image in Images(session))
        {
            InExport(image, edit, owner =>
            {
                Assert.AreEqual(Environment.ProcessId, owner.GetMethod("Read")!.Invoke(null, null));
                var native = owner.GetMethod("NativeProcessId", BindingFlags.NonPublic | BindingFlags.Static)!;
                Assert.IsTrue(native.IsPrivate);
                Assert.IsNull(native.GetMethodBody());
                var import = native.GetCustomAttribute<DllImportAttribute>()!;
                Assert.AreEqual(library, import.Value);
                Assert.AreEqual(entry, import.EntryPoint);
                Assert.AreEqual(CallingConvention.Winapi, import.CallingConvention);
                Assert.IsTrue(import.ExactSpelling);
                Assert.IsTrue(import.SetLastError);
                Assert.IsTrue(import.PreserveSig);
                Assert.HasCount(1, native.GetCustomAttributes<PreserveSigAttribute>());
            });
        }
    }

    /// <summary>
    /// Property and index signatures retain their modifiers while property and return constants remain independently readable.
    /// </summary>
    [TestMethod]
    public void Property_PreservesIndexModifiersAndReturnDefaults()
    {
        var session = new Session();
        var (assembly, _, original) = CecilFixture.Build((module, type) =>
        {
            var result = new OptionalModifierType(module.ImportReference(typeof(IsConst)), module.TypeSystem.Int32);
            var index = new RequiredModifierType(module.ImportReference(typeof(IsVolatile)), module.TypeSystem.Int32);
            var getter = new CecilMethod("get_Item", CecilMethodAttributes.Public | CecilMethodAttributes.Static
                | CecilMethodAttributes.SpecialName | CecilMethodAttributes.HideBySig, result);
            getter.Parameters.Add(new ParameterDefinition("index", CecilParameterAttributes.None, index));
            getter.MethodReturnType.Attributes = CecilParameterAttributes.HasDefault;
            getter.MethodReturnType.Constant = 29;
            type.Methods.Add(getter);
            getter.Body.GetILProcessor().Emit(OpCodes.Ldarg_0);
            getter.Body.GetILProcessor().Emit(OpCodes.Ldc_I4_1);
            getter.Body.GetILProcessor().Emit(OpCodes.Add);
            getter.Body.GetILProcessor().Emit(OpCodes.Ret);
            var property = new CecilProperty("Item", CecilPropertyAttributes.HasDefault, result)
            {
                GetMethod = getter,
                Constant = 17,
            };
            type.Properties.Add(property);
            var read = new CecilMethod("Read", CecilMethodAttributes.Public | CecilMethodAttributes.Static, module.TypeSystem.Int32);
            read.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int32));
            type.Methods.Add(read);
            read.Body.GetILProcessor().Emit(OpCodes.Ldarg_0);
            read.Body.GetILProcessor().Emit(OpCodes.Call, getter);
            read.Body.GetILProcessor().Emit(OpCodes.Ret);
        }, session.Resolver);
        AssertProperty(original);
        var edit = Commit(session, "int32 [" + assembly.GetName().Name + "]N.Fixture::Read(int32)");
        AssertProperty(edit.OriginalMethod.DeclaringType!);
        AssertProperty(edit.Method!.DeclaringType!);
        InExport(AssemblyExporter.Write(session, "property-metadata"), edit, AssertProperty);
        var source = session.ToIlAsm();
        InExport(IlasmLocator.Assemble(source), edit, owner =>
        {
            Assert.AreEqual(29, owner.GetMethod("get_Item")!.ReturnParameter.RawDefaultValue, "Microsoft ILAsm source:\n" + source);
            AssertProperty(owner);
        });
    }

    /// <summary>
    /// Property-only modifiers remain in the property signature even when its getter has ordinary unmodified types.
    /// </summary>
    [TestMethod]
    public void Property_PreservesMetadataDistinctFromItsAccessorSignature()
    {
        var session = new Session();
        var (assembly, image) = Assemble(session, """
            .class public N.Fixture extends [System.Runtime]System.Object {
              .method public specialname static int32 get_Item(int32 index) cil managed {
                ldarg.0
                ret
              }
              .property int32 modopt([System.Runtime]System.Runtime.CompilerServices.IsConst) Item(
                int32 modreq([System.Runtime]System.Runtime.CompilerServices.IsVolatile)) {
                .get int32 N.Fixture::get_Item(int32)
              }
              .method public static int32 Read(int32 index) cil managed {
                ldarg.0
                call int32 N.Fixture::get_Item(int32)
                ret
              }
            }
            """);
        AssertPropertySignature(image);
        var original = assembly.GetType("N.Fixture")!;
        Assert.AreEqual(42, original.GetProperty("Item")!.GetValue(null, [42]));
        Assert.IsEmpty(original.GetMethod("get_Item")!.ReturnParameter.GetOptionalCustomModifiers());
        Assert.IsEmpty(original.GetMethod("get_Item")!.GetParameters().Single().GetRequiredCustomModifiers());
        var edit = Commit(session, "int32 [" + assembly.GetName().Name + "]N.Fixture::Read(int32)");
        foreach (var exported in Images(session))
        {
            AssertPropertySignature(exported);
            InExport(exported, edit, owner =>
            {
                Assert.AreEqual(42, owner.GetProperty("Item")!.GetValue(null, [42]));
                Assert.IsEmpty(owner.GetMethod("get_Item")!.ReturnParameter.GetOptionalCustomModifiers());
                Assert.IsEmpty(owner.GetMethod("get_Item")!.GetParameters().Single().GetRequiredCustomModifiers());
            });
        }
    }

    /// <summary>
    /// A truncated marshal descriptor produces a field-specific preflight problem while preserving the editable method source.
    /// </summary>
    [TestMethod]
    public void MarshalDescriptor_MalformedFieldReportsTheExactMetadataBoundary()
    {
        var session = new Session();
        var (assembly, _) = Assemble(session, """
            .class public N.Fixture extends [System.Runtime]System.Object {
              .field public marshal({ 17 }) string Broken
              .method public static int32 Read() cil managed {
                ldc.i4.s 42
                ret
              }
            }
            """);
        Assert.AreEqual(42, assembly.GetType("N.Fixture")!.GetMethod("Read")!.Invoke(null, null));

        var edit = session.PrepareEdit("int32 [" + assembly.GetName().Name + "]N.Fixture::Read()", "Copy");

        Assert.IsNotEmpty(edit.Source);
        Assert.Contains("ldc.i4.s 42", edit.Source);
        Assert.Contains(problem => problem.Contains("Broken", StringComparison.Ordinal)
            && problem.Contains("marshalling", StringComparison.OrdinalIgnoreCase), edit.Problems, string.Join("\n", edit.Problems));
        Assert.ThrowsExactly<ReplException>(() => session.CommitEdit(edit.Name, edit.Source));
        Assert.IsNull(edit.Method);
        Assert.AreEqual(0, edit.Revision);
    }

    /// <summary>
    /// An explicit MethodImpl targeting a base virtual method preserves renamed private dispatch independently of interfaces.
    /// </summary>
    [TestMethod]
    public void MethodImpl_PreservesExplicitBaseClassDispatch()
    {
        var session = new Session();
        var (assembly, _, original) = CecilFixture.Build((module, type) =>
        {
            var parent = new CecilType("N", "Base", CecilTypeAttributes.NotPublic | CecilTypeAttributes.Class, module.TypeSystem.Object);
            module.Types.Add(parent);
            var baseConstructor = Constructor(module, parent, module.ImportReference(typeof(object).GetConstructor(Type.EmptyTypes)!));
            var declaration = new CecilMethod("Evaluate", CecilMethodAttributes.Public | CecilMethodAttributes.Virtual
                | CecilMethodAttributes.NewSlot, module.TypeSystem.Int32);
            parent.Methods.Add(declaration);
            declaration.Body.GetILProcessor().Emit(OpCodes.Ldc_I4_1);
            declaration.Body.GetILProcessor().Emit(OpCodes.Ret);
            type.BaseType = parent;
            var constructor = Constructor(module, type, baseConstructor);
            var body = new CecilMethod("Alternate", CecilMethodAttributes.Private | CecilMethodAttributes.Virtual
                | CecilMethodAttributes.Final | CecilMethodAttributes.NewSlot, module.TypeSystem.Int32);
            body.Overrides.Add(declaration);
            type.Methods.Add(body);
            body.Body.GetILProcessor().Emit(OpCodes.Ldc_I4, 42);
            body.Body.GetILProcessor().Emit(OpCodes.Ret);
            var read = new CecilMethod("Read", CecilMethodAttributes.Public | CecilMethodAttributes.Static, module.TypeSystem.Int32);
            type.Methods.Add(read);
            read.Body.GetILProcessor().Emit(OpCodes.Newobj, constructor);
            read.Body.GetILProcessor().Emit(OpCodes.Callvirt, declaration);
            read.Body.GetILProcessor().Emit(OpCodes.Ret);
        }, session.Resolver);
        AssertDispatch(original);
        var edit = Commit(session, "int32 [" + assembly.GetName().Name + "]N.Fixture::Read()");
        AssertDispatch(edit.OriginalMethod.DeclaringType!);
        AssertDispatch(edit.Method!.DeclaringType!);
        foreach (var image in Images(session))
        {
            InExport(image, edit, AssertDispatch);
            using var module = CecilModule.ReadModule(new MemoryStream(image));
            var owner = module.Types.Single(type => type.FullName == edit.Method.DeclaringType!.FullName);
            var implementation = owner.Methods.Single(method => method.Name == "Alternate");
            Assert.IsTrue(implementation.IsPrivate);
            Assert.HasCount(1, implementation.Overrides);
            Assert.AreEqual("Evaluate", implementation.Overrides[0].Name);
            Assert.AreEqual(owner.BaseType.FullName, implementation.Overrides[0].DeclaringType.FullName);
        }
    }

    private static CecilMethod Constructor(CecilModule module, CecilType owner, MethodReference parent)
    {
        var constructor = new CecilMethod(".ctor", CecilMethodAttributes.Public | CecilMethodAttributes.SpecialName
            | CecilMethodAttributes.RTSpecialName, module.TypeSystem.Void);
        owner.Methods.Add(constructor);
        constructor.Body.GetILProcessor().Emit(OpCodes.Ldarg_0);
        constructor.Body.GetILProcessor().Emit(OpCodes.Call, parent);
        constructor.Body.GetILProcessor().Emit(OpCodes.Ret);
        return constructor;
    }

    private static void AssertProperty(Type type)
    {
        var property = type.GetProperty("Item")!;
        Assert.AreEqual(17, property.GetRawConstantValue());
        Assert.AreEqual(typeof(IsConst), property.GetOptionalCustomModifiers().Single());
        Assert.IsEmpty(property.GetRequiredCustomModifiers());
        Assert.AreEqual(typeof(IsVolatile), property.GetIndexParameters().Single().GetRequiredCustomModifiers().Single());
        Assert.AreEqual(29, property.GetMethod!.ReturnParameter.RawDefaultValue);
        Assert.IsTrue(property.GetMethod.ReturnParameter.HasDefaultValue);
        Assert.AreEqual(ReflectionParameterAttributes.HasDefault, property.GetMethod.ReturnParameter.Attributes);
        Assert.AreEqual(42, property.GetValue(null, [41]));
        Assert.AreEqual(42, type.GetMethod("Read")!.Invoke(null, [41]));
    }

    private static void AssertDispatch(Type type)
    {
        Assert.IsEmpty(type.GetInterfaces());
        Assert.AreEqual(42, type.GetMethod("Read")!.Invoke(null, null));
        var parent = type.BaseType!;
        var declaration = parent.GetMethod("Evaluate")!;
        Assert.AreEqual(1, declaration.Invoke(Activator.CreateInstance(parent), null));
        Assert.AreEqual(42, declaration.Invoke(Activator.CreateInstance(type), null));
    }

    private static (Assembly Assembly, byte[] Image) Assemble(Session session, string body)
    {
        var name = "ExtendedMetadata" + Guid.NewGuid().ToString("N");
        var image = IlasmLocator.Assemble(".assembly extern System.Runtime {}\n.assembly " + name + " {}\n.module " + name
            + ".dll\n" + body);
        return (session.Resolver.LoadImage(image), image);
    }

    private static void AssertDescriptors(byte[] image, string typeName, byte[] expected)
    {
        using var pe = new PEReader(new MemoryStream(image));
        var metadata = pe.GetMetadataReader();
        var type = metadata.TypeDefinitions.Select(metadata.GetTypeDefinition).Single(type =>
            metadata.GetString(type.Namespace) + "." + metadata.GetString(type.Name) == typeName);
        var method = type.GetMethods().Select(metadata.GetMethodDefinition).Single(method => metadata.GetString(method.Name) == "Echo");
        var parameters = method.GetParameters().Select(metadata.GetParameter).Where(parameter => parameter.SequenceNumber < 2).ToArray();
        Assert.HasCount(2, parameters);
        foreach (var parameter in parameters)
        {
            Assert.AreSequenceEqual(expected, metadata.GetBlobBytes(parameter.GetMarshallingDescriptor()));
        }
    }

    private static void AssertPropertySignature(byte[] image)
    {
        using var pe = new PEReader(new MemoryStream(image));
        var metadata = pe.GetMetadataReader();
        var property = metadata.PropertyDefinitions.Select(metadata.GetPropertyDefinition)
            .Single(property => metadata.GetString(property.Name) == "Item");
        var signature = metadata.GetBlobReader(property.Signature);
        Assert.AreEqual(0x08, signature.ReadByte());
        Assert.AreEqual(1, signature.ReadCompressedInteger());
        Assert.AreEqual(0x20, signature.ReadByte());
        var optional = metadata.GetTypeReference((TypeReferenceHandle)signature.ReadTypeHandle());
        Assert.AreEqual("System.Runtime.CompilerServices", metadata.GetString(optional.Namespace));
        Assert.AreEqual("IsConst", metadata.GetString(optional.Name));
        Assert.AreEqual(0x08, signature.ReadByte());
        Assert.AreEqual(0x1f, signature.ReadByte());
        var required = metadata.GetTypeReference((TypeReferenceHandle)signature.ReadTypeHandle());
        Assert.AreEqual("System.Runtime.CompilerServices", metadata.GetString(required.Namespace));
        Assert.AreEqual("IsVolatile", metadata.GetString(required.Name));
        Assert.AreEqual(0x08, signature.ReadByte());
        Assert.AreEqual(0, signature.RemainingBytes);
    }

    private static MethodEdit Commit(Session session, string reference)
    {
        var edit = session.PrepareEdit(reference, "Copy");
        Assert.IsEmpty(edit.Problems, string.Join("\n", edit.Problems));
        return session.CommitEdit(edit.Name, edit.Source);
    }

    private static IEnumerable<byte[]> Images(Session session)
    {
        yield return AssemblyExporter.Write(session, "extended-imported-metadata");
        yield return IlasmLocator.Assemble(session.ToIlAsm());
    }

    private static void InExport(byte[] image, MethodEdit edit, Action<Type> inspect)
    {
        var context = new AssemblyLoadContext("extended-metadata-" + Guid.NewGuid(), isCollectible: true);
        try
        {
            var assembly = context.LoadFromStream(new MemoryStream(image));
            inspect(assembly.GetType(edit.Method!.DeclaringType!.FullName!, throwOnError: true)!);
        }
        finally
        {
            context.Unload();
        }
    }
}
