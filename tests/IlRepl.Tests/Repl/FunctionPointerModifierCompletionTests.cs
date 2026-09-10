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
/// Function-pointer completions retain supplemental calling conventions and ordinary custom modifiers.
/// </summary>
[TestClass]
public sealed class FunctionPointerModifierCompletionTests
{
    /// <summary>
    /// Supplies cancellation for completion requests.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Modifier-distinguished overloads keep their identities through completion, execution, and both export paths.
    /// </summary>
    /// <param name="shape">The pointer convention and modifier encoding.</param>
    /// <param name="generic">Whether the pointer substitutes the declaring type's argument.</param>
    [TestMethod]
    [DataRow(0, false)]
    [DataRow(1, false)]
    [DataRow(2, false)]
    [DataRow(0, true)]
    [DataRow(1, true)]
    [DataRow(2, true)]
    [DataRow(3, false)]
    [DataRow(3, true)]
    public async Task Complete_SupplementalModifiers_SelectsAndRunsEachOverload(int shape, bool generic)
    {
        var session = new Session();
        var (assembly, _, definition) = CecilFixture.Build((module, type) =>
        {
            if (generic)
            {
                type.GenericParameters.Add(new GenericParameter("T", type));
            }

            for (var index = 0; index < 2; index++)
            {
                var element = generic ? (TypeReference)type.GenericParameters[0] : module.TypeSystem.Int32;
                var pointer = new FunctionPointerType
                {
                    CallingConvention = shape == 2 ? MethodCallingConvention.Default
                        : shape == 3 && index == 1 ? MethodCallingConvention.C : MethodCallingConvention.Unmanaged,
                    ReturnType = element,
                };
                if (shape is 0 or 3)
                {
                    pointer.ReturnType = new OptionalModifierType(
                        module.ImportReference(typeof(System.Runtime.CompilerServices.CallConvCdecl)), pointer.ReturnType);
                }

                if (index == 0 && shape != 3)
                {
                    pointer.ReturnType = shape == 2
                        ? new RequiredModifierType(module.ImportReference(typeof(System.Runtime.CompilerServices.IsVolatile)),
                            pointer.ReturnType)
                        : new OptionalModifierType(module.ImportReference(
                            typeof(System.Runtime.CompilerServices.CallConvSuppressGCTransition)), pointer.ReturnType);
                }

                pointer.Parameters.Add(new ParameterDefinition(index == 0 && shape != 3
                    ? new OptionalModifierType(module.ImportReference(
                        typeof(System.Runtime.CompilerServices.IsReadOnlyAttribute)), element)
                    : element));
                var field = new FieldDefinition("Data" + index, FieldAttributes.Public | FieldAttributes.Static, pointer);
                type.Fields.Add(field);
                var fetch = new MethodDefinition("Fetch" + index, MethodAttributes.Public | MethodAttributes.Static, pointer);
                fetch.Body.Instructions.Add(Instruction.Create(OpCodes.Ldsfld, field));
                fetch.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                type.Methods.Add(fetch);
                var accept = new MethodDefinition("Accept", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Int32);
                accept.Parameters.Add(new ParameterDefinition(pointer));
                accept.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4, 7 + index));
                accept.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                type.Methods.Add(accept);
            }
        }, session.State.Resolver, "SupplementalPointers" + Guid.NewGuid().ToString("N") + (generic ? "`1" : ""));
        var fixture = generic ? definition.MakeGenericType(typeof(int)) : definition;
        var owner = $"[{assembly.GetName().Name}]{definition.FullName}" + (generic ? "<int32>" : "");
        using var snapshot = BindingSnapshot.Capture(session.State.Context);
        var scope = new SnapshotBindingScope(snapshot);
        var first = SymbolBinder.BindFieldReference(CilSyntaxParser.ParseFieldReference(owner + "::Data0"), scope).FieldType;
        var signature = first.Signature!;
        SymbolSignatureProvider.StripModifiers(signature.ReturnType, out var required, out var optional);
        var marker = RuntimeSymbolImporter.Import(shape == 2 ? typeof(System.Runtime.CompilerServices.IsVolatile)
            : shape == 3 ? typeof(System.Runtime.CompilerServices.CallConvCdecl)
            : typeof(System.Runtime.CompilerServices.CallConvSuppressGCTransition));
        Assert.Contains(marker, shape == 2 ? required : optional);
        Assert.AreEqual(shape != 2, signature.IsExtensibleUnmanaged);
        Assert.AreEqual(shape == 3 ? TypeSymbolKind.Primitive : TypeSymbolKind.Modified, signature.Parameters.Single().Kind);
        Assert.AreEqual(first, RuntimeSymbolImporter.Import(fixture.GetField("Data0")!).FieldType);
        using var completer = new OperandCompleter(session);
        var prefix = $"call {owner}::Accept";
        var reply = await completer.CompleteAsync(new CompletionRequest([prefix], 0, prefix.Length, null, []),
            TestContext.CancellationToken);
        Assert.HasCount(2, reply.Items);
        for (var index = 0; index < 2; index++)
        {
            var item = reply.Items.Single(item => shape == 3
                ? item.InsertText.Contains("unmanaged cdecl", StringComparison.Ordinal) == (index == 1)
                : item.InsertText.Contains("IsReadOnlyAttribute", StringComparison.Ordinal) == (index == 0));
            var call = prefix[..reply.ReplaceStart] + item.InsertText;
            var body = new[] { $"ldsfld {owner}::Data{index}", "pop", $"call {owner}::Fetch{index}()", call };
            foreach (var line in body)
            {
                session.AddLine(line);
            }

            Assert.AreEqual(7 + index, session.Run().Value);
            session.AddLine(".method int32 Check() {");
            foreach (var line in body)
            {
                session.AddLine(line);
            }

            session.AddLine("ret");
            session.AddLine("}");
            session.AddLine("call Check");
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".dll");
            try
            {
                session.Save(path);
                foreach (var image in new[] { File.ReadAllBytes(path), IlasmLocator.Assemble(session.ToIlAsm()) })
                {
                    var context = new System.Runtime.Loader.AssemblyLoadContext("pointer-modifier-export", isCollectible: true);
                    context.Resolving += (_, name) => name.Name == assembly.GetName().Name ? assembly : null;
                    try
                    {
                        var exported = context.LoadFromStream(new MemoryStream(image));
                        Assert.AreEqual(7 + index, exported.GetType("IlRepl.Cell")!.GetMethod("Run")!.Invoke(null, null));
                    }
                    finally
                    {
                        context.Unload();
                    }
                }
            }
            finally
            {
                File.Delete(path);
            }

            Assert.AreEqual(7 + index, session.Run().Value);
        }
    }
}
