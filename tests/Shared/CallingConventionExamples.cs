using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace IlRepl.Tests.Shared;

/// <summary>
/// Provides real method definitions whose edited calling convention can add or remove optional arguments.
/// </summary>
public static class CallingConventionExamples
{
    /// <summary>
    /// Emits a method that returns 42 with the requested original calling convention.
    /// </summary>
    /// <param name="vararg">Whether the original method uses managed varargs.</param>
    /// <returns>The complete portable executable image.</returns>
    public static byte[] Create(bool vararg)
    {
        var metadata = new MetadataBuilder();
        var name = "Convention" + Guid.NewGuid().ToString("N");
        metadata.AddModule(0, metadata.GetOrAddString(name), metadata.GetOrAddGuid(Guid.NewGuid()), default, default);
        metadata.AddAssembly(metadata.GetOrAddString(name), new Version(1, 0, 0, 0), default, default, 0, AssemblyHashAlgorithm.None);
        var core = typeof(object).Assembly.GetName();
        var reference = metadata.AddAssemblyReference(metadata.GetOrAddString(core.Name!), core.Version!, default,
            metadata.GetOrAddBlob(core.GetPublicKeyToken()!), 0, default);
        var objectType = metadata.AddTypeReference(reference, metadata.GetOrAddString("System"), metadata.GetOrAddString("Object"));
        metadata.AddTypeDefinition(0, default, metadata.GetOrAddString("<Module>"), default,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        metadata.AddTypeDefinition(TypeAttributes.Public, default, metadata.GetOrAddString("Owner"), objectType,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        var instructions = new InstructionEncoder(new BlobBuilder());
        instructions.LoadConstantI4(42);
        instructions.OpCode(ILOpCode.Ret);
        var bodies = new BlobBuilder();
        var offset = new MethodBodyStreamEncoder(bodies).AddMethodBody(instructions);
        byte[] signature = [vararg ? (byte)5 : (byte)0, 0, 8];
        metadata.AddMethodDefinition(MethodAttributes.Public | MethodAttributes.Static, MethodImplAttributes.IL,
            metadata.GetOrAddString("Read"), metadata.GetOrAddBlob(signature), offset, MetadataTokens.ParameterHandle(1));
        var image = new BlobBuilder();
        new ManagedPEBuilder(new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage | Characteristics.Dll),
            new MetadataRootBuilder(metadata), bodies, flags: CorFlags.ILOnly).Serialize(image);
        return image.ToArray();
    }

    /// <summary>
    /// Uses arglist when adding varargs so execution proves the emitted convention matches the edited header.
    /// </summary>
    /// <param name="vararg">Whether the edited method accepts optional arguments.</param>
    /// <returns>The complete edited method declaration.</returns>
    public static string Method(bool vararg) => ".method public static " + (vararg ? "vararg " : "") + "int32 Read() {\n"
        + (vararg ? """
          .locals init (valuetype ArgIterator iterator)
          arglist
          newobj instance void ArgIterator::.ctor(valuetype RuntimeArgumentHandle)
          stloc.0
          ldloca.s 0
          call instance int32 ArgIterator::GetRemainingCount()
          ldc.i4.s 42
          add
          ret
        }
        """ : "ldc.i4.s 42\nret\n}");
}
