using System.Runtime.Loader;
using IlRepl.Engine;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Emitted stack headers retain Cecil's sufficient bounds and receive larger independently validated limits.
/// </summary>
[TestClass]
public sealed class CecilStackHeadersTests
{
    /// <summary>
    /// Empty and sufficient limits leave the image unchanged, while a larger limit changes only the method header.
    /// </summary>
    /// <param name="limit">The validated limit, or null when no correction was requested.</param>
    [TestMethod]
    [DataRow(null)]
    [DataRow(1)]
    [DataRow(32)]
    public void Apply_PreservesVerifiedExecutableBodies(int? limit)
    {
        using var assembly = CreateAssembly(fatHeader: true);
        var method = assembly.MainModule.Types.Single(type => type.Name == "Program").Methods.Single();
        using var stream = new MemoryStream();
        assembly.Write(stream);
        var image = stream.ToArray();
        var original = image.ToArray();
        Assert.AreEqual(1, method.Body.MaxStackSize);
        var limits = new Dictionary<MethodDefinition, int>();
        if (limit.HasValue)
        {
            limits.Add(method, limit.Value);
        }

        CecilStackHeaders.Apply(image, limits);

        using var before = ModuleDefinition.ReadModule(new MemoryStream(original));
        using var after = ModuleDefinition.ReadModule(new MemoryStream(image));
        var originalBody = before.Types.Single(type => type.Name == "Program").Methods.Single().Body;
        var correctedBody = after.Types.Single(type => type.Name == "Program").Methods.Single().Body;
        Assert.AreEqual(limit ?? 1, correctedBody.MaxStackSize);
        Assert.AreSequenceEqual(originalBody.Instructions.Select(instruction => instruction.ToString()).ToArray(),
            correctedBody.Instructions.Select(instruction => instruction.ToString()).ToArray());
        if (limit is null or 1)
        {
            Assert.AreSequenceEqual(original, image);
        }

        using var verifier = new IlVerificationOracle();
        Assert.IsEmpty(verifier.Verify(image));
        var context = new AssemblyLoadContext(null, isCollectible: true);
        try
        {
            var loaded = context.LoadFromStream(new MemoryStream(image));
            Assert.AreEqual(42, loaded.GetType("Program")!.GetMethod("Main")!.Invoke(null, null));
        }
        finally
        {
            context.Unload();
        }
    }

    /// <summary>
    /// A tiny header refuses a graph-derived limit that its implicit eight-slot bound cannot represent.
    /// </summary>
    [TestMethod]
    public void Apply_RejectsUnrepresentableTinyHeader()
    {
        using var assembly = CreateAssembly(fatHeader: false);
        var method = assembly.MainModule.Types.Single(type => type.Name == "Program").Methods.Single();
        using var stream = new MemoryStream();
        assembly.Write(stream);
        var image = stream.ToArray();
        var original = image.ToArray();
        using var emitted = ModuleDefinition.ReadModule(new MemoryStream(image));
        Assert.AreEqual(8, emitted.Types.Single(type => type.Name == "Program").Methods.Single().Body.MaxStackSize);

        var failure = Assert.ThrowsExactly<ReplException>(() =>
            CecilStackHeaders.Apply(image, new Dictionary<MethodDefinition, int> { [method] = 9 }));

        Assert.AreEqual("a tiny method header cannot represent the required evaluation stack", failure.Message);
        Assert.AreSequenceEqual(original, image);
    }

    /// <summary>
    /// Computed bounds at the fat-header limit survive, while an overflowing bound cannot bypass the checked correction.
    /// </summary>
    /// <param name="depth">The actual evaluation-stack depth of the emitted instructions.</param>
    [TestMethod]
    [DataRow(65535)]
    [DataRow(65536)]
    public void Apply_ChecksComputedFatHeaderRange(int depth)
    {
        using var assembly = CreateAssembly(fatHeader: true);
        var method = assembly.MainModule.Types.Single(type => type.Name == "Program").Methods.Single();
        var body = method.Body;
        body.Instructions.Clear();
        var processor = body.GetILProcessor();
        for (var index = 0; index < depth; index++)
        {
            processor.Emit(OpCodes.Ldc_I4_0);
        }

        for (var index = 1; index < depth; index++)
        {
            processor.Emit(OpCodes.Pop);
        }

        processor.Emit(OpCodes.Ret);
        using var stream = new MemoryStream();
        assembly.Write(stream);
        Assert.AreEqual(depth, body.MaxStackSize);
        var image = stream.ToArray();
        var limits = new Dictionary<MethodDefinition, int> { [method] = depth };
        if (depth > ushort.MaxValue)
        {
            Assert.ThrowsExactly<OverflowException>(() => CecilStackHeaders.Apply(image, limits));
            return;
        }

        CecilStackHeaders.Apply(image, limits);
        using var emitted = ModuleDefinition.ReadModule(new MemoryStream(image));
        Assert.AreEqual(depth, emitted.Types.Single(type => type.Name == "Program").Methods.Single().Body.MaxStackSize);
    }

    private static AssemblyDefinition CreateAssembly(bool fatHeader)
    {
        var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("StackHeaderFixture", new Version(1, 0)),
            "StackHeaderFixture", ModuleKind.Dll);
        var module = assembly.MainModule;
        var type = new TypeDefinition("", "Program", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed,
            module.ImportReference(typeof(object)));
        module.Types.Add(type);
        var method = new MethodDefinition("Main", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Int32);
        type.Methods.Add(method);
        var body = method.Body.GetILProcessor();
        if (fatHeader)
        {
            for (var index = 0; index < 64; index++)
            {
                body.Emit(OpCodes.Nop);
            }
        }

        body.Emit(OpCodes.Ldc_I4, 42);
        body.Emit(OpCodes.Ret);
        return assembly;
    }
}
