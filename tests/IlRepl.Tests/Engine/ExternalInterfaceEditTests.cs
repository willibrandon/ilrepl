using System.Runtime.Loader;
using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Tests.Shared;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace IlRepl.Tests.Engine;

/// <summary>
/// External base interfaces retain their original identity in copied casts, calls, comparisons, and saved assemblies.
/// </summary>
[TestClass]
public sealed class ExternalInterfaceEditTests
{
    /// <summary>
    /// Supplies cancellation for real comparison workers.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// A public base keeps source-legal internal member access even when it has no private interface requiring an access grant.
    /// </summary>
    /// <param name="operation">The inherited internal member operation.</param>
    [TestMethod]
    [DataRow("internal")]
    [DataRow("field")]
    public void Edit_InternalExternalMembersPreserveAssemblyAccess(string operation)
    {
        var fixture = ExternalInterfaceFixture.Create(operation, false, false);
        using var moduleStream = new MemoryStream(fixture.Image);
        using var module = ModuleDefinition.ReadModule(moduleStream);
        var parent = module.GetType("ExternalBase");
        parent.Interfaces.Clear();
        foreach (var method in parent.Methods)
        {
            method.Overrides.Clear();
        }

        using var image = new MemoryStream();
        module.Write(image);
        var session = new Session();
        var original = session.Resolver.LoadImage(image.ToArray());
        Assert.AreEqual(42, original.GetType("Owner")!.GetMethod("Probe")!.Invoke(null, null));
        var edit = session.PrepareEdit("int32 Owner::Probe()", "Copy");
        Assert.IsEmpty(edit.Problems, string.Join('\n', edit.Problems));
        session.CommitEdit(edit.Name, edit.Source);
        Assert.AreEqual(42, edit.Method!.Invoke(null, null));
        Assert.AreEqual(original.GetType("ExternalBase"), edit.Method.DeclaringType!.BaseType);
        Assert.IsEmpty(edit.Method.DeclaringType.GetInterfaces());
    }

    /// <summary>
    /// Module initialization can require copying a base after its interface types were first retained as external dependencies.
    /// </summary>
    [TestMethod]
    public void Edit_ModuleInitializationCopiesTheBaseAndItsInterfaceTogether()
    {
        var fixture = ExternalInterfaceFixture.Create("castclass", true, false);
        using var moduleStream = new MemoryStream(fixture.Image);
        using var module = ModuleDefinition.ReadModule(moduleStream);
        var initialize = new MethodDefinition(".cctor", MethodAttributes.Private | MethodAttributes.Static
            | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName, module.TypeSystem.Void);
        module.Types[0].Methods.Add(initialize);
        var il = initialize.Body.GetILProcessor();
        il.Emit(OpCodes.Newobj, module.GetType("ExternalBase").Methods.Single(method => method.IsConstructor));
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ret);
        using var image = new MemoryStream();
        module.Write(image);
        var session = new Session();
        var original = session.Resolver.LoadImage(image.ToArray());
        Assert.AreEqual(42, original.GetType("Owner")!.GetMethod("Probe")!.Invoke(null, null));
        var edit = session.PrepareEdit("int32 Owner::Probe()", "Copy");
        Assert.IsEmpty(edit.Problems, string.Join('\n', edit.Problems));
        session.CommitEdit(edit.Name, edit.Source);
        Assert.AreEqual(42, edit.Method!.Invoke(null, null));
        Assert.AreNotEqual(original.GetType("ExternalBase"), edit.Method.DeclaringType!.BaseType);
        Assert.AreNotEqual(original.GetType("Owner")!.GetInterfaces().Single(), edit.Method.DeclaringType.GetInterfaces().Single());
        session.AddLine("call Copy");
        var saved = AssemblyExporter.Write(session, "copied-interface-base");
        var context = new AssemblyLoadContext("copied-interface-base", isCollectible: true);
        try
        {
            var exported = context.LoadImage(saved);
            Assert.DoesNotContain(reference => reference.Name == fixture.Name, exported.GetReferencedAssemblies());
            Assert.AreEqual(42, exported.GetType("IlRepl.Cell")!.GetMethod("Run")!.Invoke(null, null));
        }
        finally
        {
            context.Unload();
        }
    }

    /// <summary>
    /// A base contract requiring the original owner is diagnosed before a copy can report an altered baseline as equivalent.
    /// </summary>
    [TestMethod]
    public void Prepare_ExternalContractRequiringOriginalOwnerReportsTheIdentityConflict()
    {
        var fixture = ExternalInterfaceFixture.Create("isinst", true, false, ownerArgument: true);
        var session = new Session();
        var original = session.Resolver.LoadImage(fixture.Image);
        Assert.AreEqual(42, original.GetType("Owner")!.GetMethod("Probe")!.Invoke(null, null));
        var edit = session.PrepareEdit("int32 Owner::Probe()", "Copy");
        Assert.IsNull(edit.Method);
        Assert.Contains(problem => problem.Contains("external base", StringComparison.Ordinal)
            && problem.Contains("original nominal type", StringComparison.Ordinal), edit.Problems);
    }

    /// <summary>
    /// A private inherited contract remains implemented by the original external base and any explicit derived implementation.
    /// </summary>
    /// <param name="operation">The selected cast or interface call.</param>
    /// <param name="generic">Whether the interface requires an original private type argument.</param>
    /// <param name="reimplement">Whether the derived type reimplements the contract.</param>
    [TestMethod]
    [DataRow("isinst", false, false)]
    [DataRow("isinst", true, false)]
    [DataRow("castclass", false, false)]
    [DataRow("castclass", true, false)]
    [DataRow("callvirt", false, false)]
    [DataRow("callvirt", true, false)]
    [DataRow("isinst", true, true)]
    [DataRow("castclass", false, true)]
    [DataRow("castclass", true, true)]
    [DataRow("callvirt", true, true)]
    [DataRow("base-isinst", false, false)]
    [DataRow("base-isinst", true, false)]
    [DataRow("internal", false, false)]
    [DataRow("internal", true, false)]
    [DataRow("field", false, false)]
    [DataRow("field", true, false)]
    public async Task Compare_InheritedExternalInterfacesKeepTheirIdentity(string operation, bool generic, bool reimplement)
    {
        var fixture = ExternalInterfaceFixture.Create(operation, generic, reimplement);
        var session = new Session();
        var original = session.Resolver.LoadImage(fixture.Image);
        var expected = reimplement && operation != "isinst" ? 43 : 42;
        var edit = session.PrepareEdit("int32 Owner::Probe()", "Copy");
        Assert.AreEqual(expected, edit.Original.Requested.Invoke(null, null));
        Assert.IsEmpty(edit.Problems, string.Join('\n', edit.Problems));
        session.CommitEdit(edit.Name, edit.Source);
        Assert.AreEqual(expected, edit.Method!.Invoke(null, null));
        Assert.AreEqual(original.GetType("ExternalBase"), edit.Method.DeclaringType!.BaseType);
        Assert.AreEqual(original.GetType("Owner")!.GetInterfaces().Single(), edit.Method.DeclaringType.GetInterfaces().Single());
        var unchanged = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy ()"),
            TestContext.CancellationToken);
        Assert.AreEqual("match", unchanged.Outcome, unchanged.Original.Detail + "; " + unchanged.Edited.Detail);
        Assert.AreEqual(expected.ToString(), unchanged.Original.Result!.Value);
        Assert.AreEqual(expected.ToString(), unchanged.Edited.Result!.Value);
        session.CommitEdit(edit.Name, edit.Source.Replace("ret", "ldc.i4.1\nadd\nret", StringComparison.Ordinal));
        var changed = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy ()"),
            TestContext.CancellationToken);
        Assert.AreEqual("different", changed.Outcome, changed.Original.Detail + "; " + changed.Edited.Detail);
        Assert.AreEqual(expected.ToString(), changed.Original.Result!.Value);
        Assert.AreEqual((expected + 1).ToString(), changed.Edited.Result!.Value);
        session.AddLine("call Copy");
        foreach (var image in new[] { AssemblyExporter.Write(session, "external-interfaces"), IlasmLocator.Assemble(session.ToIlAsm()) })
        {
            using var moduleStream = new MemoryStream(image);
            using var module = ModuleDefinition.ReadModule(moduleStream);
            Assert.DoesNotContain(type => type.IsInterface, module.GetTypes());
            var context = new AssemblyLoadContext("external-interfaces", isCollectible: true);
            context.Resolving += (_, name) => name.Name == fixture.Name
                ? context.LoadImage(fixture.Image) : null;
            try
            {
                var saved = context.LoadImage(image);
                Assert.AreEqual(expected + 1, saved.GetType("IlRepl.Cell")!.GetMethod("Run")!.Invoke(null, null));
            }
            finally
            {
                context.Unload();
            }
        }
    }
}
