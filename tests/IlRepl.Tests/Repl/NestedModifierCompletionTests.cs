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
/// Nested custom modifiers survive completion, live binding, and emitted member references.
/// </summary>
[TestClass]
public sealed class NestedModifierCompletionTests
{
    /// <summary>
    /// Supplies cancellation for completion requests.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Array and pointer elements keep their modifiers independently of root parameter modifiers.
    /// </summary>
    /// <param name="shape">The surrounding array, pointer, or byref shape.</param>
    /// <param name="required">Whether the nested modifier is required.</param>
    /// <param name="generic">Whether the element substitutes a declaring type parameter.</param>
    [TestMethod]
    [DataRow(0, true, false)]
    [DataRow(0, false, false)]
    [DataRow(1, true, false)]
    [DataRow(2, false, false)]
    [DataRow(3, true, false)]
    [DataRow(0, true, true)]
    public async Task Complete_NestedModifiers_BindsAndRuns(int shape, bool required, bool generic)
    {
        var session = new Session();
        var (assembly, _, definition) = CecilFixture.Build((module, type) =>
        {
            if (generic)
            {
                type.GenericParameters.Add(new GenericParameter("T", type));
            }

            var element = generic ? (TypeReference)type.GenericParameters[0] : module.TypeSystem.Int32;
            var modifier = module.ImportReference(typeof(System.Runtime.CompilerServices.IsVolatile));
            var modified = required ? (TypeReference)new RequiredModifierType(modifier, element)
                : new OptionalModifierType(modifier, element);
            var fieldType = shape == 2 ? (TypeReference)new PointerType(modified) : new ArrayType(modified, shape == 1 ? 2 : 1);
            var root = module.ImportReference(typeof(System.Runtime.CompilerServices.IsReadOnlyAttribute));
            var field = new FieldDefinition("Data", FieldAttributes.Public | FieldAttributes.Static,
                new OptionalModifierType(root, fieldType));
            type.Fields.Add(field);
            var signatureType = new OptionalModifierType(root, shape == 3 ? new ByReferenceType(fieldType) : fieldType);
            var fetch = new MethodDefinition("Fetch", MethodAttributes.Public | MethodAttributes.Static, signatureType);
            fetch.Body.Instructions.Add(Instruction.Create(shape == 3 ? OpCodes.Ldsflda : OpCodes.Ldsfld, field));
            fetch.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            type.Methods.Add(fetch);
            var accept = new MethodDefinition("Accept", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Int32);
            accept.Parameters.Add(new ParameterDefinition(signatureType));
            accept.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_7));
            accept.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            type.Methods.Add(accept);
        }, session.State.Resolver, "NestedModifiers" + Guid.NewGuid().ToString("N") + (generic ? "`1" : ""));
        var fixture = generic ? definition.MakeGenericType(typeof(int)) : definition;
        var owner = $"[{assembly.GetName().Name}]{definition.FullName}" + (generic ? "<int32>" : "");
        using var snapshot = BindingSnapshot.Capture(session.State.Context);
        var scope = new SnapshotBindingScope(snapshot);
        var expectedField = SymbolBinder.BindFieldReference(CilSyntaxParser.ParseFieldReference(owner + "::Data"), scope);
        var actualField = RuntimeSymbolImporter.Import(fixture.GetField("Data")!);
        Assert.AreEqual(expectedField.FieldType, actualField.FieldType);
        Assert.HasCount(1, actualField.OptionalModifiers);
        var expectedReturn = SymbolBinder.BindMethodReference(CilSyntaxParser.ParseMethodReference(owner + "::Fetch"), scope, false);
        var actualReturn = RuntimeSymbolImporter.Import(fixture.GetMethod("Fetch")!);
        Assert.AreEqual(expectedReturn.Method.ReturnType, actualReturn.ReturnType);
        Assert.HasCount(1, actualReturn.ReturnOptionalModifiers);
        Assert.AreEqual(actualReturn.ReturnType, RuntimeSymbolImporter.Import(fixture.GetMethod("Accept")!).Parameters.Single().Type);

        using var completer = new OperandCompleter(session);
        var body = new List<string>();
        foreach (var prefix in new[] { $"ldsfld {owner}::Data", $"call {owner}::Fetch", $"call {owner}::Accept" })
        {
            var reply = await completer.CompleteAsync(new CompletionRequest([prefix], 0, prefix.Length, null, []),
                TestContext.CancellationToken);
            Assert.HasCount(1, reply.Items, prefix);
            body.Add(prefix[..reply.ReplaceStart] + reply.Items[0].InsertText + prefix[(reply.ReplaceStart + reply.ReplaceLength)..]);
            if (body.Count == 1)
            {
                body.Add("pop");
            }
        }

        foreach (var named in new[] { false, true })
        {
            if (named)
            {
                session.AddLine(".method int32 Check() {");
            }

            foreach (var line in body)
            {
                session.AddLine(line);
            }

            if (named)
            {
                session.AddLine("ret");
                session.AddLine("}");
                session.AddLine("call Check");
            }

            Assert.AreEqual(7, session.Run().Value);
        }

        session.AddLine("call Check");
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".dll");
        try
        {
            session.Save(path);
            foreach (var image in new[] { File.ReadAllBytes(path), IlasmLocator.Assemble(session.ToIlAsm()) })
            {
                var context = new System.Runtime.Loader.AssemblyLoadContext("nested-modifier-export", isCollectible: true);
                context.Resolving += (_, name) => name.Name == assembly.GetName().Name ? assembly : null;
                try
                {
                    var exported = context.LoadFromStream(new MemoryStream(image));
                    Assert.AreEqual(7, exported.GetType("IlRepl.Cell")!.GetMethod("Run")!.Invoke(null, null));
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
    }
}
