using IlRepl.Engine;
using IlRepl.Engine.Binding;
using IlRepl.Protocol;
using IlRepl.Repl;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Instruction = Mono.Cecil.Cil.Instruction;

namespace IlRepl.Tests.Repl;

/// <summary>
/// Array sizes remain distinct when metadata omits a lower bound instead of explicitly encoding zero.
/// </summary>
[TestClass]
public sealed class SizeOnlyArrayCompletionTests
{
    /// <summary>
    /// Supplies cancellation for completion requests.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Both array encodings remain independently selectable and execute the intended overloaded method.
    /// </summary>
    /// <param name="shape">Whether the fixture uses an ordinary, generic-owner, or generic-method reference.</param>
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public async Task Complete_OmittedLowerBound_PreservesBothOverloads(int shape)
    {
        var session = new Session();
        var assembly = session.State.Resolver.LoadImage(BuildFixture(shape));
        var definition = assembly.GetExportedTypes().Single();
        var fixture = shape == 1 ? definition.MakeGenericType(typeof(int)) : definition;
        var missing = RuntimeSymbolImporter.Import(fixture.GetField("Data0")!).FieldType;
        var zero = RuntimeSymbolImporter.Import(fixture.GetField("Data1")!).FieldType;
        Assert.AreSequenceEqual<int>([3], missing.Sizes);
        Assert.IsEmpty(missing.LowerBounds);
        Assert.AreSequenceEqual<int>([0], zero.LowerBounds);
        Assert.AreNotEqual(missing, zero);
        using var completer = new OperandCompleter(session);
        var owner = $"[{assembly.GetName().Name}]{definition.FullName}" + (shape == 1 ? "<int32>" : "");
        var prefix = $"call {owner}::Accept" + (shape == 2 ? "<int32>" : "");
        var reply = await completer.CompleteAsync(new CompletionRequest([prefix], 0, prefix.Length, null, []),
            TestContext.CancellationToken);
        Assert.HasCount(2, reply.Items);
        var results = new HashSet<int>();
        foreach (var item in reply.Items)
        {
            session.ClearCell();
            session.AddLine("ldnull");
            var line = prefix[..reply.ReplaceStart] + item.InsertText + prefix[(reply.ReplaceStart + reply.ReplaceLength)..];
            session.AddLine(line);
            var expected = (int)session.Run().Value!;
            results.Add(expected);
            session.ClearCell();
            session.AddLine(".method int32 Check() {");
            var fieldPrefix = $"ldsfld {owner}::Data" + (expected == 7 ? "0" : "1");
            var fieldReply = await completer.CompleteAsync(new CompletionRequest([fieldPrefix], 0, fieldPrefix.Length, null, []),
                TestContext.CancellationToken);
            Assert.HasCount(1, fieldReply.Items);
            session.AddLine(fieldPrefix[..fieldReply.ReplaceStart] + fieldReply.Items[0].InsertText);
            session.AddLine(line);
            session.AddLine("ret");
            session.AddLine("}");
            session.AddLine("call Check");
            Assert.AreEqual(expected, session.Run().Value);
            session.AddLine("call Check");
            if (expected == 7)
            {
                var error = Assert.Throws<ReplException>(() => session.ToIlAsm());
                Assert.Contains("use .save for exact metadata", error.Message);
            }

            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".dll");
            var context = new System.Runtime.Loader.AssemblyLoadContext("size-only-export", isCollectible: true);
            context.Resolving += (_, requested) => requested.Name == assembly.GetName().Name ? assembly : null;
            try
            {
                session.Save(path);
                var exported = context.LoadFromStream(new MemoryStream(File.ReadAllBytes(path)));
                Assert.AreEqual(expected, exported.GetType("IlRepl.Cell")!.GetMethod("Run")!.Invoke(null, null));
            }
            finally
            {
                context.Unload();
                File.Delete(path);
            }
        }

        Assert.AreSequenceEqual<int>([7, 8], results.Order());
    }

    private static byte[] BuildFixture(int shape)
    {
        var name = "SizeOnly" + Guid.NewGuid().ToString("N");
        using var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition(name, new Version(1, 0)), name, ModuleKind.Dll);
        var module = assembly.MainModule;
        var type = new TypeDefinition("N", "Fixture" + name + (shape == 1 ? "`1" : ""), TypeAttributes.Public, module.TypeSystem.Object);
        module.Types.Add(type);
        if (shape == 1)
        {
            type.GenericParameters.Add(new GenericParameter("T", type));
        }

        for (var index = 0; index < 2; index++)
        {
            var element = shape == 1 ? (TypeReference)type.GenericParameters[0] : module.TypeSystem.Int32;
            var array = new ArrayType(element);
            array.Dimensions[0] = index == 0 ? new ArrayDimension(1, 3) : new ArrayDimension(0, 2);
            type.Fields.Add(new FieldDefinition("Data" + index, FieldAttributes.Public | FieldAttributes.Static, array));
            var method = new MethodDefinition("Accept", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Int32);
            if (shape == 2)
            {
                method.GenericParameters.Add(new GenericParameter("T", method));
            }

            method.Parameters.Add(new ParameterDefinition(array));
            method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4, 7 + index));
            method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            type.Methods.Add(method);
        }

        using var stream = new MemoryStream();
        assembly.Write(stream);
        var image = stream.ToArray();
        // Cecil invents zero lower bounds for size-only dimensions. Patch the controlled fixture's shape instead.
        var marker = shape == 1
            ? new byte[] { 0x14, 0x13, 0, 1, 1, 3, 1, 2 }
            : new byte[] { 0x14, 0x08, 1, 1, 3, 1, 2 };
        for (var position = 0; position < image.Length;)
        {
            var offset = image.AsSpan(position).IndexOf(marker);
            if (offset < 0)
            {
                break;
            }

            position += offset;
            image[position + marker.Length - 2] = 0;
            image[position + marker.Length - 1] = 0;
            position += marker.Length;
        }

        return image;
    }
}
