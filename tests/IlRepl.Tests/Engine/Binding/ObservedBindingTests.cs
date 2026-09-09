using System.Runtime.Loader;
using IlRepl.Engine;
using IlRepl.Engine.Binding;
using Mono.Cecil;

namespace IlRepl.Tests.Engine.Binding;

/// <summary>
/// Metadata binding preserves load-context and version identity and learns only from actual runtime operations.
/// </summary>
[TestClass]
public sealed class ObservedBindingTests
{
    /// <summary>
    /// An unknown custom-context edge stays unresolved until real field binding records the selected assembly instance.
    /// </summary>
    [TestMethod]
    public void CustomBinding_IsObservedWithoutInvokingThePolicyDuringCapture()
    {
        var dependencyName = "CompletionDependency" + Guid.NewGuid().ToString("N");
        var image = Dependency(dependencyName, new Version(1, 0, 0, 0));
        var first = AssemblyLoadContext.Default.LoadFromStream(new MemoryStream(image));
        var secondContext = new AssemblyLoadContext("completion-alternate", isCollectible: true);
        var second = secondContext.LoadFromStream(new MemoryStream(image));
        var requesterContext = new BindingProbeContext(dependencyName, second);
        try
        {
            var requester = requesterContext.LoadFromStream(new MemoryStream(Requester(dependencyName, new Version(1, 0, 0, 0))));
            var source = AssemblySymbolSource.For(requester)!;
            var assemblies = new[] { requester, first, second, typeof(object).Assembly };
            var sources = assemblies.Select(assembly => (assembly, AssemblySymbolSource.For(assembly)!)).ToArray();
            var before = new LoadedBindingCatalog(sources);
            var owner = before.FindType(source, "U", "Holder")!;
            var handle = source.TypeHandleOf(owner.Definition)!.Value;
            Assert.IsTrue(source.Fields(handle, before).Single().FieldType.HasUnresolved);
            Assert.AreEqual(0, requesterContext.Calls);

            var actualOwner = requester.GetType("U.Holder")!;
            var resolver = new TypeResolver();
            var runtime = new RuntimeBindingScope(new ParseContext([], [], GenericContext.Empty, resolver, []));
            var observed = runtime.Fields(runtime.ImportType(actualOwner)).Single();
            Assert.AreEqual(RuntimeDefinitions.AssemblyInstance(second), observed.FieldType.Definition.Assembly);
            Assert.IsGreaterThan(0, requesterContext.Calls);
            var calls = requesterContext.Calls;

            var after = new LoadedBindingCatalog(sources);
            var retained = source.Fields(handle, after).Single();
            Assert.AreEqual(observed.FieldType, retained.FieldType);
            Assert.AreNotEqual(RuntimeDefinitions.AssemblyInstance(first), retained.FieldType.Definition.Assembly);
            Assert.AreEqual(calls, requesterContext.Calls);
            Assert.IsTrue(source.Fields(handle, before).Single().FieldType.HasUnresolved, "Existing snapshots retain their own graph.");
        }
        finally
        {
            requesterContext.Unload();
            secondContext.Unload();
        }
    }

    /// <summary>
    /// Loaded-version unification agrees with the runtime and never substitutes a lower version for a higher reference.
    /// </summary>
    [TestMethod]
    [DataRow(1, 2, true)]
    [DataRow(2, 2, true)]
    [DataRow(3, 2, false)]
    public void AssemblyVersion_MatchesTheLoadedRuntimeRule(int requestedVersion, int loadedVersion, bool allowed)
    {
        var name = "CompletionVersion" + Guid.NewGuid().ToString("N");
        var context = new AssemblyLoadContext(name, isCollectible: true);
        try
        {
            var dependency = context.LoadFromStream(new MemoryStream(Dependency(name, new Version(loadedVersion, 0, 0, 0))));
            var requester = context.LoadFromStream(new MemoryStream(Requester(name, new Version(requestedVersion, 0, 0, 0))));
            var source = AssemblySymbolSource.For(requester)!;
            var catalog = new LoadedBindingCatalog(new[] { requester, dependency, typeof(object).Assembly }
                .Select(assembly => (assembly, AssemblySymbolSource.For(assembly)!)));
            var owner = catalog.FindType(source, "U", "Holder")!;
            var symbol = source.Fields(source.TypeHandleOf(owner.Definition)!.Value, catalog).Single().FieldType;
            Assert.AreEqual(allowed, !symbol.HasUnresolved);
            var field = requester.GetType("U.Holder")!.GetField("Value")!;
            if (allowed)
            {
                Assert.AreEqual(RuntimeSymbolImporter.Import(field.FieldType), symbol);
            }
            else
            {
                Assert.ThrowsExactly<FileNotFoundException>(() => _ = field.FieldType);
            }
        }
        finally
        {
            context.Unload();
        }
    }

    /// <summary>
    /// Equal interface names from different assemblies retain distinct identities after runtime observation.
    /// </summary>
    /// <param name="constraints">Whether the references appear as constraints instead of implemented interfaces.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RuntimeObservation_DuplicateInterfaceNames_PreserveAssemblyIdentity(bool constraints)
    {
        var name = "ObservedInterfaces" + Guid.NewGuid().ToString("N");
        var context = new AssemblyLoadContext(name, isCollectible: true);
        try
        {
            var version = new Version(1, 0, 0, 0);
            var dependencies = new[] { name + "First", name + "Second" }.Select(dependency =>
                context.LoadFromStream(new MemoryStream(Image(dependency, version, module => module.Types.Add(
                    new TypeDefinition("N", "I", Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Interface
                        | Mono.Cecil.TypeAttributes.Abstract)))))).ToArray();
            var requester = context.LoadFromStream(new MemoryStream(Image(name, version, module =>
            {
                var owner = new TypeDefinition("N", "Owner", Mono.Cecil.TypeAttributes.Public,
                    module.ImportReference(typeof(object)));
                var parameter = new GenericParameter("T", owner);
                if (constraints)
                {
                    owner.GenericParameters.Add(parameter);
                }

                foreach (var dependency in dependencies)
                {
                    var reference = new AssemblyNameReference(dependency.GetName().Name!, version);
                    module.AssemblyReferences.Add(reference);
                    var type = new TypeReference("N", "I", module, reference);
                    if (constraints)
                    {
                        parameter.Constraints.Add(new GenericParameterConstraint(type));
                    }
                    else
                    {
                        owner.Interfaces.Add(new InterfaceImplementation(type));
                    }
                }

                module.Types.Add(owner);
            })));
            var actualOwner = requester.GetType("N.Owner")!;
            var runtime = new RuntimeBindingScope(new ParseContext([], [], GenericContext.Empty, new TypeResolver(), []));
            var ownerSymbol = runtime.ImportType(actualOwner);
            var observed = constraints
                ? runtime.GenericParameterDeclarations(ownerSymbol).Single().Constraints
                : runtime.DeclaredInterfacesOf(ownerSymbol);
            Assert.HasCount(2, observed.Select(type => type.Definition).Distinct().ToArray());

            var source = AssemblySymbolSource.For(requester)!;
            var catalog = new LoadedBindingCatalog(dependencies.Append(requester).Append(typeof(object).Assembly)
                .Select(assembly => (assembly, AssemblySymbolSource.For(assembly)!)));
            var handle = source.TypeHandleOf(ownerSymbol.Definition)!.Value;
            var retained = constraints ? source.GenericParameters(handle, catalog).Single().Constraints
                : source.Interfaces(handle, catalog);
            Assert.AreSequenceEqual(observed, retained);
        }
        finally
        {
            context.Unload();
        }
    }

    private static byte[] Dependency(string name, Version version) => Image(name, version, module =>
        module.Types.Add(new TypeDefinition("D", "Value", Mono.Cecil.TypeAttributes.Public, module.ImportReference(typeof(object)))));

    private static byte[] Requester(string dependency, Version version) => Image("CompletionRequester" + Guid.NewGuid().ToString("N"),
        new Version(1, 0, 0, 0), module =>
        {
            var reference = new AssemblyNameReference(dependency, version);
            module.AssemblyReferences.Add(reference);
            var type = new TypeDefinition("U", "Holder", Mono.Cecil.TypeAttributes.Public, module.ImportReference(typeof(object)));
            type.Fields.Add(new FieldDefinition("Value", Mono.Cecil.FieldAttributes.Public | Mono.Cecil.FieldAttributes.Static,
                new TypeReference("D", "Value", module, reference)));
            module.Types.Add(type);
        });

    private static byte[] Image(string name, Version version, Action<ModuleDefinition> populate)
    {
        using var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition(name, version), name, ModuleKind.Dll);
        populate(assembly.MainModule);
        using var stream = new MemoryStream();
        assembly.Write(stream);
        return stream.ToArray();
    }
}
