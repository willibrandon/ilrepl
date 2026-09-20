using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using IlRepl.Engine;
using Mono.Cecil;
using CecilFieldAttributes = Mono.Cecil.FieldAttributes;
using CecilFieldDefinition = Mono.Cecil.FieldDefinition;
using CecilMethodAttributes = Mono.Cecil.MethodAttributes;
using CecilMethodDefinition = Mono.Cecil.MethodDefinition;
using CecilModule = Mono.Cecil.ModuleDefinition;
using CecilOpCodes = Mono.Cecil.Cil.OpCodes;
using CecilParameterAttributes = Mono.Cecil.ParameterAttributes;
using CecilTypeAttributes = Mono.Cecil.TypeAttributes;
using CecilTypeDefinition = Mono.Cecil.TypeDefinition;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Imported declarations preserve exact native marshaling descriptors and retained module initialization bytes.
/// </summary>
[TestClass]
public sealed class ImportedMetadataTests
{
    /// <summary>
    /// Copied fixed-array and fixed-string fields retain their descriptors and actual unmanaged memory layout.
    /// </summary>
    [TestMethod]
    public void Commit_FixedFieldMarshallingPreservesBytesAndNativeLayout()
    {
        var session = new Session();
        var (assembly, image, original) = CecilFixture.Build((module, type) =>
        {
            type.Attributes |= CecilTypeAttributes.SequentialLayout | CecilTypeAttributes.UnicodeClass;
            type.PackingSize = 1;
            type.ClassSize = 16;
            type.Fields.Add(new CecilFieldDefinition("Values", CecilFieldAttributes.Public | CecilFieldAttributes.HasFieldMarshal,
                new ArrayType(module.TypeSystem.Int16))
            {
                MarshalInfo = new FixedArrayMarshalInfo { Size = 3, ElementType = NativeType.I2 },
            });

            type.Fields.Add(new CecilFieldDefinition("Name", CecilFieldAttributes.Public | CecilFieldAttributes.HasFieldMarshal,
                module.TypeSystem.String)
            {
                MarshalInfo = new FixedSysStringMarshalInfo { Size = 5 },
            });

            var method = new CecilMethodDefinition("Value", CecilMethodAttributes.Public | CecilMethodAttributes.Static,
                module.TypeSystem.Int32);
            type.Methods.Add(method);
            method.Body.GetILProcessor().Emit(CecilOpCodes.Ldc_I4, 42);
            method.Body.GetILProcessor().Emit(CecilOpCodes.Ret);
        }, session.Resolver);

        var edit = Commit(session, $"int32 [{assembly.GetName().Name}]N.Fixture::Value()");
        Assert.AreEqual(42, edit.Method!.Invoke(null, null));
        AssertNativeLayout(original);
        AssertNativeLayout(edit.OriginalMethod.DeclaringType!);
        AssertNativeLayout(edit.Method.DeclaringType!);
        foreach (var exported in Images(session, "field-marshalling"))
        {
            AssertMarshalBlobs(image, original.FullName!, exported, edit.Method.DeclaringType!.FullName!);
            InExport(exported, edit.Method.DeclaringType.FullName!, AssertNativeLayout);
        }
    }

    /// <summary>
    /// Array descriptors retain omitted optional fields and explicitly encoded size controls on parameters and returns.
    /// </summary>
    /// <param name="details">How many descriptor fields the original metadata contains.</param>
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public void Commit_ArrayMarshallingPreservesExactOptionalDescriptorFields(int details)
    {
        var session = new Session();
        var (assembly, image, original) = CecilFixture.Build((module, type) =>
        {
            var array = new ArrayType(module.TypeSystem.Int32);
            var method = new CecilMethodDefinition("Echo", CecilMethodAttributes.Public | CecilMethodAttributes.Static, array);
            type.Methods.Add(method);
            method.Parameters.Add(new ParameterDefinition("values", CecilParameterAttributes.HasFieldMarshal, array)
            {
                MarshalInfo = ArrayDescriptor(details),
            });

            method.Parameters.Add(new ParameterDefinition("count", CecilParameterAttributes.None, module.TypeSystem.Int32));
            method.MethodReturnType.MarshalInfo = ArrayDescriptor(details);
            method.MethodReturnType.Attributes = CecilParameterAttributes.HasFieldMarshal;
            method.Body.GetILProcessor().Emit(CecilOpCodes.Ldarg_0);
            method.Body.GetILProcessor().Emit(CecilOpCodes.Ret);
        }, session.Resolver);

        var edit = Commit(session, $"int32[] [{assembly.GetName().Name}]N.Fixture::Echo(int32[], int32)");
        int[] values = [10, 20, 30];
        Assert.AreSame(values, original.GetMethod("Echo")!.Invoke(null, [values, 3]));
        Assert.AreSame(values, edit.OriginalMethod.Invoke(null, [values, 3]));
        Assert.AreSame(values, edit.Method!.Invoke(null, [values, 3]));
        foreach (var exported in Images(session, "array-marshalling"))
        {
            AssertMarshalBlobs(image, original.FullName!, exported, edit.Method.DeclaringType!.FullName!);
            InExport(exported, edit.Method.DeclaringType.FullName!, type =>
                Assert.AreSame(values, type.GetMethod("Echo")!.Invoke(null, [values, 3])));
        }
    }

    /// <summary>
    /// Scalar marshaling descriptors remain attached to the return parameter and ordinary parameters.
    /// </summary>
    [TestMethod]
    public void Commit_ScalarReturnAndParameterMarshallingRemainExact()
    {
        var session = new Session();
        var (assembly, image, original) = CecilFixture.Build((module, type) =>
        {
            var method = new CecilMethodDefinition("Echo", CecilMethodAttributes.Public | CecilMethodAttributes.Static,
                module.TypeSystem.Boolean);
            type.Methods.Add(method);
            method.Parameters.Add(new ParameterDefinition("value", CecilParameterAttributes.In | CecilParameterAttributes.HasFieldMarshal,
                module.TypeSystem.Boolean)
            {
                MarshalInfo = new MarshalInfo(NativeType.I1),
            });

            method.MethodReturnType.MarshalInfo = new MarshalInfo(NativeType.U1);
            method.MethodReturnType.Attributes = CecilParameterAttributes.HasFieldMarshal;
            method.Body.GetILProcessor().Emit(CecilOpCodes.Ldarg_0);
            method.Body.GetILProcessor().Emit(CecilOpCodes.Ret);
        }, session.Resolver);

        var edit = Commit(session, $"bool [{assembly.GetName().Name}]N.Fixture::Echo(bool)");
        var method = (MethodInfo)edit.Method!;
        Assert.IsTrue((bool)method.Invoke(null, [true])!);
        Assert.IsFalse((bool)method.Invoke(null, [false])!);
        Assert.IsTrue(method.GetParameters().Single().IsIn);
        Assert.AreEqual(UnmanagedType.I1, method.GetParameters().Single().GetCustomAttribute<MarshalAsAttribute>()!.Value);
        Assert.AreEqual(UnmanagedType.U1, method.ReturnParameter.GetCustomAttribute<MarshalAsAttribute>()!.Value);

        foreach (var exported in Images(session, "scalar-marshalling"))
        {
            AssertMarshalBlobs(image, original.FullName!, exported, method.DeclaringType!.FullName!);
            InExport(exported, method.DeclaringType.FullName!, type =>
                Assert.IsTrue((bool)type.GetMethod("Echo")!.Invoke(null, [true])!));
        }
    }

    /// <summary>
    /// Resolver-only images retain exact primitive and explicit-layout RVA data used by the real runtime array initializer.
    /// </summary>
    /// <param name="structured">Whether the data field uses an explicit-size value type instead of a primitive.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Commit_RetainedImagePreservesRvaBytesAndActualArrayInitialization(bool structured)
    {
        var session = new Session();
        int[] expected = structured ? [0x11223344, 0x55667711] : [0x11223344];
        var bytes = expected.SelectMany(BitConverter.GetBytes).ToArray();
        var (assembly, image, original) = CecilFixture.Build((module, type) =>
        {
            var fieldType = module.TypeSystem.Int32;
            if (structured)
            {
                var blob = new CecilTypeDefinition("", "BlobData", CecilTypeAttributes.NestedPrivate
                    | CecilTypeAttributes.Sealed | CecilTypeAttributes.ExplicitLayout, module.ImportReference(typeof(ValueType)))
                {
                    PackingSize = 1,
                    ClassSize = bytes.Length,
                };

                type.NestedTypes.Add(blob);
                fieldType = blob;
            }

            var field = new CecilFieldDefinition("Data", CecilFieldAttributes.Private | CecilFieldAttributes.Static
                | CecilFieldAttributes.InitOnly | CecilFieldAttributes.HasFieldRVA, fieldType) { InitialValue = bytes };
            type.Fields.Add(field);
            var method = new CecilMethodDefinition("Read", CecilMethodAttributes.Public | CecilMethodAttributes.Static,
                new ArrayType(module.TypeSystem.Int32));
            type.Methods.Add(method);
            var il = method.Body.GetILProcessor();
            il.Emit(CecilOpCodes.Ldc_I4, expected.Length);
            il.Emit(CecilOpCodes.Newarr, module.TypeSystem.Int32);
            il.Emit(CecilOpCodes.Dup);
            il.Emit(CecilOpCodes.Ldtoken, field);
            il.Emit(CecilOpCodes.Call, module.ImportReference(typeof(RuntimeHelpers).GetMethod(nameof(RuntimeHelpers.InitializeArray))!));
            il.Emit(CecilOpCodes.Ret);
        }, session.Resolver);

        Assert.IsEmpty(assembly.Location);
        Assert.IsFalse(SessionAssemblies.TryGetDefinition(assembly, out _));
        Assert.IsTrue(session.Resolver.TryGetImage(assembly, out var retained));
        Assert.AreSequenceEqual(image, retained);
        Assert.AreSequenceEqual(expected, (int[])original.GetMethod("Read")!.Invoke(null, null)!);
        var edit = Commit(session, $"int32[] [{assembly.GetName().Name}]N.Fixture::Read()");
        Assert.AreSequenceEqual(expected, (int[])edit.OriginalMethod.Invoke(null, null)!);
        Assert.AreSequenceEqual(expected, (int[])edit.Method!.Invoke(null, null)!);
        foreach (var exported in Images(session, "retained-rva"))
        {
            using var moduleStream = new MemoryStream(exported);
            using var module = CecilModule.ReadModule(moduleStream);
            var owner = module.Types.Single(type => type.FullName == edit.Method.DeclaringType!.FullName);
            var data = owner.Fields.Single(field => field.Name == "Data");
            Assert.IsTrue(data.IsInitOnly);
            Assert.AreNotEqual(0, data.RVA);
            Assert.AreSequenceEqual(bytes, data.InitialValue);
            InExport(exported, edit.Method.DeclaringType!.FullName!, type =>
                Assert.AreSequenceEqual(expected, (int[])type.GetMethod("Read")!.Invoke(null, null)!));
        }
    }

    /// <summary>
    /// Reading retained RVA metadata never runs a data owner's static initializer, even when that initializer would throw.
    /// </summary>
    [TestMethod]
    public void Commit_RvaMetadataDoesNotInitializeItsDeclaringType()
    {
        var session = new Session();
        var (assembly, _, original) = CecilFixture.Build((module, type) =>
        {
            var storage = new CecilTypeDefinition("", "Storage", CecilTypeAttributes.NestedPrivate, module.TypeSystem.Object);
            type.NestedTypes.Add(storage);
            var field = new CecilFieldDefinition("Data", CecilFieldAttributes.Public | CecilFieldAttributes.Static
                | CecilFieldAttributes.InitOnly | CecilFieldAttributes.HasFieldRVA, module.TypeSystem.Int32)
            {
                InitialValue = BitConverter.GetBytes(42),
            };

            storage.Fields.Add(field);
            var initializer = new CecilMethodDefinition(".cctor", CecilMethodAttributes.Private | CecilMethodAttributes.Static
                | CecilMethodAttributes.SpecialName | CecilMethodAttributes.RTSpecialName, module.TypeSystem.Void);
            storage.Methods.Add(initializer);
            var setup = initializer.Body.GetILProcessor();
            setup.Emit(CecilOpCodes.Ldstr, "data owner initialized");
            setup.Emit(CecilOpCodes.Newobj, module.ImportReference(typeof(InvalidOperationException).GetConstructor([typeof(string)])!));
            setup.Emit(CecilOpCodes.Throw);
            var method = new CecilMethodDefinition("Read", CecilMethodAttributes.Public | CecilMethodAttributes.Static,
                new ArrayType(module.TypeSystem.Int32));
            type.Methods.Add(method);
            var il = method.Body.GetILProcessor();
            il.Emit(CecilOpCodes.Ldc_I4_1);
            il.Emit(CecilOpCodes.Newarr, module.TypeSystem.Int32);
            il.Emit(CecilOpCodes.Dup);
            il.Emit(CecilOpCodes.Ldtoken, field);
            il.Emit(CecilOpCodes.Call, module.ImportReference(typeof(RuntimeHelpers).GetMethod(nameof(RuntimeHelpers.InitializeArray))!));
            il.Emit(CecilOpCodes.Ret);
        }, session.Resolver);

        int[] expected = [42];
        Assert.AreSequenceEqual(expected, (int[])original.GetMethod("Read")!.Invoke(null, null)!);

        var edit = Commit(session, $"int32[] [{assembly.GetName().Name}]N.Fixture::Read()");

        Assert.AreSequenceEqual(expected, (int[])edit.OriginalMethod.Invoke(null, null)!);
        Assert.AreSequenceEqual(expected, (int[])edit.Method!.Invoke(null, null)!);
        foreach (var image in Images(session, "rva-with-initializer"))
        {
            InExport(image, edit.Method.DeclaringType!.FullName!, type =>
                Assert.AreSequenceEqual(expected, (int[])type.GetMethod("Read")!.Invoke(null, null)!));
        }

        var originalData = original.GetNestedType("Storage", BindingFlags.NonPublic)!.GetField("Data")!;
        var failure = Assert.ThrowsExactly<TargetInvocationException>(() => originalData.GetValue(null));
        Assert.IsInstanceOfType<TypeInitializationException>(failure.InnerException);
        Assert.IsInstanceOfType<InvalidOperationException>(failure.InnerException.InnerException);
        Assert.AreEqual("data owner initialized", failure.InnerException.InnerException.Message);
    }

    private static ArrayMarshalInfo ArrayDescriptor(int details) => new()
    {
        ElementType = details == 0 ? NativeType.None : NativeType.I4,
        SizeParameterIndex = details == 2 ? 1 : -1,
        Size = details == 2 ? 3 : -1,
        SizeParameterMultiplier = details == 2 ? 1 : -1,
    };

    private static MethodEdit Commit(Session session, string reference)
    {
        var edit = session.PrepareEdit(reference, "Copy");
        Assert.IsEmpty(edit.Problems, string.Join("\n", edit.Problems));
        return session.CommitEdit(edit.Name, edit.Source);
    }

    private static IEnumerable<byte[]> Images(Session session, string name)
    {
        yield return AssemblyExporter.Write(session, name);
        yield return IlasmLocator.Assemble(IlAsmRenderer.Render(session));
    }

    private static void AssertNativeLayout(Type type)
    {
        Assert.AreEqual(16, Marshal.SizeOf(type));
        Assert.AreEqual(0, Marshal.OffsetOf(type, "Values").ToInt32());
        Assert.AreEqual(6, Marshal.OffsetOf(type, "Name").ToInt32());
        var value = RuntimeHelpers.GetUninitializedObject(type);
        short[] values = [11, 22, 33];
        type.GetField("Values")!.SetValue(value, values);
        type.GetField("Name")!.SetValue(value, "Hi");
        var address = Marshal.AllocHGlobal(16);
        try
        {
            Marshal.StructureToPtr(value, address, fDeleteOld: false);
            Assert.AreEqual(11, Marshal.ReadInt16(address, 0));
            Assert.AreEqual(22, Marshal.ReadInt16(address, 2));
            Assert.AreEqual(33, Marshal.ReadInt16(address, 4));
            Assert.AreEqual((short)'H', Marshal.ReadInt16(address, 6));
            Assert.AreEqual((short)'i', Marshal.ReadInt16(address, 8));
            Assert.AreEqual(0, Marshal.ReadInt16(address, 10));
        }
        finally
        {
            Marshal.FreeHGlobal(address);
        }
    }

    private static void AssertMarshalBlobs(byte[] original, string originalType, byte[] exported, string exportedType)
    {
        var expected = MarshalBlobs(original, originalType);
        var actual = MarshalBlobs(exported, exportedType);
        Assert.IsNotEmpty(expected);
        Assert.AreSequenceEqual(expected.Keys.Order(StringComparer.Ordinal), actual.Keys.Order(StringComparer.Ordinal));
        foreach (var (name, bytes) in expected)
        {
            Assert.AreSequenceEqual(bytes, actual[name], name);
        }
    }

    private static Dictionary<string, byte[]> MarshalBlobs(byte[] image, string typeName)
    {
        using var pe = new PEReader(new MemoryStream(image));
        var reader = pe.GetMetadataReader();
        var type = reader.TypeDefinitions.Select(reader.GetTypeDefinition).Single(type =>
            reader.GetString(type.Namespace) + "." + reader.GetString(type.Name) == typeName);
        var descriptors = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var handle in type.GetFields())
        {
            var field = reader.GetFieldDefinition(handle);
            if (field.GetMarshallingDescriptor() is { IsNil: false } descriptor)
            {
                descriptors.Add("field " + reader.GetString(field.Name), reader.GetBlobBytes(descriptor));
            }
        }

        foreach (var handle in type.GetMethods())
        {
            var method = reader.GetMethodDefinition(handle);
            foreach (var parameterHandle in method.GetParameters())
            {
                var parameter = reader.GetParameter(parameterHandle);
                if (parameter.GetMarshallingDescriptor() is { IsNil: false } descriptor)
                {
                    descriptors.Add(reader.GetString(method.Name) + " parameter " + parameter.SequenceNumber,
                        reader.GetBlobBytes(descriptor));
                }
            }
        }

        return descriptors;
    }

    private static void InExport(byte[] image, string typeName, Action<Type> inspect)
    {
        var context = new AssemblyLoadContext("imported-metadata-" + Guid.NewGuid(), isCollectible: true);
        try
        {
            var assembly = context.LoadImage(image);
            inspect(assembly.GetType(typeName, throwOnError: true)!);
        }
        finally
        {
            context.Unload();
        }
    }
}
