using IlRepl.Engine;
using IlRepl.Engine.Binding;
using IlRepl.Protocol;
using IlRepl.Repl;
using IlRepl.Tests.Engine;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Instruction = Mono.Cecil.Cil.Instruction;

namespace IlRepl.Tests.Repl;

/// <summary>
/// Array completion preserves metadata bounds through selection, binding, and execution.
/// </summary>
[TestClass]
public sealed class ArrayCompletionTests
{
    /// <summary>
    /// Supplies cancellation for completion requests.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Overloads differing only by array bounds remain distinct in both live cells and persisted method bodies.
    /// </summary>
    /// <param name="shape">The fixture's array layout.</param>
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    public async Task Complete_BoundedArrays_SelectsAndRunsEachOverload(int shape)
    {
        var session = new Session();
        var (assembly, _, definition) = CecilFixture.Build((module, type) =>
        {
            if (shape == 3)
            {
                type.GenericParameters.Add(new GenericParameter("T", type));
            }

            for (var index = 0; index < 2; index++)
            {
                var element = shape == 3 ? (TypeReference)type.GenericParameters[0] : module.TypeSystem.Int32;
                var array = new ArrayType(element, shape == 2 ? 2 : 1);
                array.Dimensions[0] = shape == 1 ? new ArrayDimension(0, 2 + index) : new ArrayDimension(1 + index, 4 + index);
                if (shape == 2)
                {
                    array.Dimensions[1] = new ArrayDimension(-2 - index, 1 - index);
                }

                var field = new FieldDefinition("Data" + index, FieldAttributes.Public | FieldAttributes.Static, array);
                type.Fields.Add(field);
                var method = new MethodDefinition("Accept", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Int32);
                if (shape == 4)
                {
                    method.GenericParameters.Add(new GenericParameter("T", method));
                }

                method.Parameters.Add(new ParameterDefinition(array));
                method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4, 7 + index));
                method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                type.Methods.Add(method);
                var fetch = new MethodDefinition("Fetch" + index, MethodAttributes.Public | MethodAttributes.Static, array);
                fetch.Body.Instructions.Add(Instruction.Create(OpCodes.Ldsfld, field));
                fetch.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                type.Methods.Add(fetch);
            }
        }, session.State.Resolver, "BoundedArrays" + Guid.NewGuid().ToString("N") + (shape == 3 ? "`1" : ""));
        var fixture = shape == 3 ? definition.MakeGenericType(typeof(int)) : definition;
        using var completer = new OperandCompleter(session);
        var owner = $"[{assembly.GetName().Name}]{definition.FullName}" + (shape == 3 ? "<int32>" : "");
        var prefix = $"call {owner}::Accept" + (shape == 4 ? "<int32>" : "");
        var reply = await completer.CompleteAsync(new CompletionRequest([prefix], 0, prefix.Length, null, []),
            TestContext.CancellationToken);
        Assert.HasCount(2, reply.Items);
        using var captured = BindingSnapshot.Capture(session.State.Context);
        var scope = new SnapshotBindingScope(captured);
        var speller = new TypeSpeller(scope);
        for (var index = 0; index < 2; index++)
        {
            var field = fixture.GetField("Data" + index)!;
            var fieldSyntax = CilSyntaxParser.ParseFieldReference(owner + "::Data" + index);
            var expected = SymbolBinder.BindFieldReference(fieldSyntax, scope).FieldType;
            var spelling = speller.Spell(expected);
            var expectedSpelling = shape switch
            {
                0 or 3 or 4 => index == 0 ? "int32[1...4]" : "int32[2...5]",
                1 => index == 0 ? "int32[3]" : "int32[4]",
                _ => index == 0 ? "int32[1...4,-2...1]" : "int32[2...5,-3...0]",
            };
            Assert.AreEqual(expectedSpelling, spelling);
            Assert.AreEqual(expected, RuntimeSymbolImporter.Import(field).FieldType);
            Assert.AreEqual(expected, RuntimeSymbolImporter.Import(fixture.GetMethod("Fetch" + index)!).ReturnType);
            var item = reply.Items.Single(candidate => candidate.InsertText.EndsWith("(" + spelling + ")", StringComparison.Ordinal));
            var line = prefix[..reply.ReplaceStart] + item.InsertText + prefix[(reply.ReplaceStart + reply.ReplaceLength)..];
            var resolved = MemberResolver.ResolveMethod(line[5..], session.State.Context, false).Method!;
            Assert.AreEqual(expected, RuntimeSymbolImporter.Import(resolved).Parameters.Single().Type);
            Assert.AreEqual(7 + index, resolved.Invoke(null, [null]));

            var fieldLine = await Complete(completer, $"ldsfld {owner}::Data{index}");
            var fetchLine = await Complete(completer, $"call {owner}::Fetch{index}");
            foreach (var named in new[] { false, true })
            {
                session.ClearCell();
                if (shape == 4 && !named && index == 0)
                {
                    session.AddLine(".typeparams (TCell)");
                    session.AddLine(".typeargs (int32)");
                }

                if (named)
                {
                    session.AddLine(".method int32 Check() {");
                }

                session.AddLine(fieldLine);
                session.AddLine("pop");
                session.AddLine(fetchLine);
                session.AddLine(line);
                if (named)
                {
                    session.AddLine("ret");
                    session.AddLine("}");
                    session.AddLine("call Check");
                }

                Assert.AreEqual(7 + index, session.Run().Value, $"{spelling}, named={named}");
            }

            session.AddLine("call Check");
            var image = IlasmLocator.Assemble(session.ToIlAsm());
            var context = new System.Runtime.Loader.AssemblyLoadContext("array-roundtrip", isCollectible: true);
            context.Resolving += (_, name) => name.Name == assembly.GetName().Name ? assembly : null;
            try
            {
                var exported = context.LoadFromStream(new MemoryStream(image));
                var run = exported.GetType("IlRepl.Cell")!.GetMethod("Run")!;
                if (run.IsGenericMethodDefinition)
                {
                    run = run.MakeGenericMethod(typeof(int));
                }

                Assert.AreEqual(7 + index, run.Invoke(null, null));
            }
            finally
            {
                context.Unload();
            }
        }
    }

    /// <summary>
    /// Releasing a cell also releases the metadata body used for an exact generic array reference.
    /// </summary>
    [TestMethod]
    [DoNotParallelize]
    public void Release_CollectsTheMetadataCellBody()
    {
        var session = new Session();
        var (assembly, _, fixture) = CecilFixture.Build((module, type) =>
        {
            var array = new ArrayType(module.TypeSystem.Int32);
            array.Dimensions[0] = new ArrayDimension(1, 4);
            var method = new MethodDefinition("Accept", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Int32);
            method.GenericParameters.Add(new GenericParameter("T", method));
            method.Parameters.Add(new ParameterDefinition(array));
            method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_7));
            method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            type.Methods.Add(method);
        }, session.State.Resolver, "ArrayLifetime" + Guid.NewGuid().ToString("N"));
        session.AddLine("ldnull");
        session.AddLine($"call [{assembly.GetName().Name}]{fixture.FullName}::Accept<int32>(int32[1...4])");
        var weak = CompileAndRelease(session);
        for (var attempt = 0; attempt < 10 && weak.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        Assert.IsFalse(weak.IsAlive, "The released cell must not retain its metadata body assembly.");
        GC.KeepAlive(session);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference CompileAndRelease(Session session)
    {
        var compiled = CellCompiler.Compile(session);
        try
        {
            Assert.AreEqual(7, compiled.Invoke(null));
            var body = compiled.Definition.Dependencies.Single(dependency => dependency.Kind == SessionAssemblyKind.Cell);
            return new WeakReference(body.Assembly);
        }
        finally
        {
            compiled.Release();
        }
    }

    private async Task<string> Complete(OperandCompleter completer, string line)
    {
        var reply = await completer.CompleteAsync(new CompletionRequest([line], 0, line.Length, null, []),
            TestContext.CancellationToken);
        Assert.HasCount(1, reply.Items, line);
        return line[..reply.ReplaceStart] + reply.Items[0].InsertText + line[(reply.ReplaceStart + reply.ReplaceLength)..];
    }
}
