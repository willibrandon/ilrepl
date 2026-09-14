using System.Runtime.Loader;
using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Tests.Shared;
using Mono.Cecil;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Types mentioned only in exact member signatures remain pinned and export without source-session references.
/// </summary>
[TestClass]
public sealed class MethodEditSignatureTypeTests
{
    /// <summary>
    /// Supplies cancellation for real comparison workers.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Copied modifiers survive type replacement, revisions, isolated execution, and independent PE and ILAsm exports.
    /// </summary>
    /// <param name="shape">The signature position containing the modifier.</param>
    /// <param name="required">Whether the modifier is required.</param>
    /// <returns>The completed dependency, metadata, and execution assertions.</returns>
    [TestMethod]
    [DataRow("parameter", false)]
    [DataRow("parameter", true)]
    [DataRow("return", false)]
    [DataRow("return", true)]
    [DataRow("field", false)]
    [DataRow("field", true)]
    [DataRow("property", false)]
    [DataRow("property", true)]
    [DataRow("nested", false)]
    [DataRow("nested", true)]
    [DataRow("abstract", false)]
    [DataRow("abstract", true)]
    [DataRow("constructor", false)]
    [DataRow("constructor", true)]
    public async Task Commit_ExactMemberSignature_RemainsIndependent(string shape, bool required)
    {
        var session = IlLines.Load(SignatureDependencyExamples.Source(shape, required).Split('\n'));
        var edit = session.PrepareEdit("Owner::Read", "Copy");
        session.CommitEdit(edit.Name, edit.Source.Replace("ret", "ldc.i4.1\nadd\nret", StringComparison.Ordinal));
        Assert.Contains(dependency => dependency.Symbol == "Marker" && dependency.Disposition == "copied (distinct type identity)",
            edit.Dependencies);
        foreach (var line in IlLines.Expand(".class public Marker { .field public int64 Added; }"))
        {
            session.AddLine(line);
        }

        session.CommitEdit(edit.Name, edit.Source);
        var marker = edit.Method!.Module.Assembly.GetTypes().Single(type => type.GetField("Original") is not null);
        Assert.IsNull(marker.GetField("Added"));
        var comparison = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy (41)"),
            TestContext.CancellationToken);
        Assert.AreEqual("different", comparison.Outcome, comparison.Original.Detail + "; " + comparison.Edited.Detail);
        Assert.AreEqual("41", comparison.Original.Result!.Value);
        Assert.AreEqual("42", comparison.Edited.Result!.Value);
        session.AddLine("ldc.i4.s 41");
        session.AddLine("call Copy");
        foreach (var image in new[] { AssemblyExporter.Write(session, "signature-types"), IlasmLocator.Assemble(session.ToIlAsm()) })
        {
            using var module = ModuleDefinition.ReadModule(new MemoryStream(image));
            var owner = module.GetTypes().Single(type => type.FullName == edit.Method.DeclaringType!.FullName);
            var signatures = owner.Fields.Select(field => field.FieldType)
                .Concat(owner.Properties.Select(property => property.PropertyType))
                .Concat(owner.Methods.SelectMany(method => method.Parameters.Select(parameter => parameter.ParameterType)
                    .Prepend(method.ReturnType)));
            var modifiers = signatures.SelectMany(Modifiers).ToArray();
            Assert.HasCount(shape == "abstract" ? 2 : 1, modifiers);
            foreach (var modifier in modifiers)
            {
                Assert.AreEqual(required, modifier is RequiredModifierType);
                Assert.AreEqual(marker.FullName, modifier.ModifierType.FullName);
                Assert.AreSame(module, modifier.ModifierType.Scope);
            }

            var context = new AssemblyLoadContext("signature-types-export", isCollectible: true);
            try
            {
                var exported = context.LoadFromStream(new MemoryStream(image));
                Assert.AreEqual(42, exported.GetType("IlRepl.Cell")!.GetMethod("Run")!.Invoke(null, null));
            }
            finally
            {
                context.Unload();
            }
        }
    }

    /// <summary>
    /// A modifier first introduced by an edited signature is captured before independent export.
    /// </summary>
    /// <param name="returned">Whether the modifier belongs to the return type rather than the argument.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Commit_IntroducedModifier_IsDiscovered(bool returned)
    {
        var session = IlLines.Load(SignatureDependencyExamples.Source("plain", false).Split('\n'));
        var edit = session.PrepareEdit("Owner::Read", "Copy");
        var source = returned ? edit.Source.Replace("int32 Read", "int32 modopt(Marker) Read", StringComparison.Ordinal)
            : edit.Source.Replace("(int32 ", "(int32 modopt(Marker) ", StringComparison.Ordinal);
        session.CommitEdit(edit.Name, source);
        foreach (var line in IlLines.Expand(".class public Marker { .field public int64 Added; }"))
        {
            session.AddLine(line);
        }

        session.AddLine("ldc.i4.s 42");
        session.AddLine("call Copy");
        foreach (var image in new[] { AssemblyExporter.Write(session, "introduced-modifier"), IlasmLocator.Assemble(session.ToIlAsm()) })
        {
            var context = new AssemblyLoadContext("introduced-modifier-export", isCollectible: true);
            try
            {
                var exported = context.LoadFromStream(new MemoryStream(image));
                var method = exported.GetType(edit.Method!.DeclaringType!.FullName!)!.GetMethod("Read")!;
                var parameter = returned ? method.ReturnParameter : method.GetParameters().Single();
                var modifier = parameter.GetOptionalCustomModifiers().Single();
                Assert.AreSame(exported, modifier.Assembly);
                Assert.IsNotNull(modifier.GetField("Original"));
                Assert.IsNull(modifier.GetField("Added"));
                Assert.AreEqual(42, exported.GetType("IlRepl.Cell")!.GetMethod("Run")!.Invoke(null, null));
            }
            finally
            {
                context.Unload();
            }
        }
    }

    private static IEnumerable<IModifierType> Modifiers(TypeReference type)
    {
        if (type is IModifierType modifier)
        {
            yield return modifier;
        }

        if (type is FunctionPointerType pointer)
        {
            foreach (var nested in pointer.Parameters.Select(parameter => parameter.ParameterType).Prepend(pointer.ReturnType)
                .SelectMany(Modifiers))
            {
                yield return nested;
            }
        }
        else if (type is TypeSpecification specification)
        {
            foreach (var nested in Modifiers(specification.ElementType))
            {
                yield return nested;
            }
        }
    }
}
