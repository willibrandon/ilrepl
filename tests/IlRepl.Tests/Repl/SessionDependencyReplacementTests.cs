using System.Reflection;
using IlRepl.Engine;
using IlRepl.Protocol;
using IlRepl.Repl;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Instruction = Mono.Cecil.Cil.Instruction;

namespace IlRepl.Tests.Repl;

/// <summary>
/// Dependency adoption removes obsolete bindings while preserving unaffected identities and rejecting bound replacements.
/// </summary>
[TestClass]
public sealed class SessionDependencyReplacementTests
{
    /// <summary>
    /// A removed image disappears from both name lookup and inherited contexts even when no replacement image exists.
    /// </summary>
    [TestMethod]
    public void AdoptReferences_RemovesLastImageAndAllowsExplicitReintroduction()
    {
        using var fixture = new SessionDependencyFixture();
        var image = Image(fixture, fixture.AssemblyName);
        var document = Document(image);
        using var core = new ReplCore();
        core.AdoptReferences(document);
        var previous = core.Session.Resolver.LoadedAssemblies.Single();
        var generation = core.Session.Generation;

        core.AdoptReferences(new SessionDocument());

        Assert.IsEmpty(core.Session.Resolver.LoadedAssemblies);
        Assert.IsEmpty(core.CaptureSession(new SessionEditor()).References);
        Assert.IsGreaterThan(generation, core.Session.Generation);
        Assert.ThrowsExactly<ReplException>(() => core.Session.Resolver.Resolve("DependencySamples.Values", fixture.AssemblyName));
        using (core.Session.Resolver.EnterContext())
        {
            Assert.IsNull(ReferenceLoadScope.Current!.Resolve(previous.GetName()));
            Assert.IsNull(ReferenceLoadScope.Current.FindLoaded(previous.GetName()));
        }

        core.AdoptReferences(document);
        var reintroduced = core.Session.Resolver.LoadedAssemblies.Single();
        Assert.AreNotSame(previous, reintroduced);
        Assert.AreEqual(42, reintroduced.GetType("DependencySamples.Values")!.GetMethod("Read")!.Invoke(null, null));
    }

    /// <summary>
    /// An unchanged consumer cannot reach removed dependencies through an earlier context, including after another adoption.
    /// </summary>
    /// <param name="origin">Whether the images belong to a package or project manifest.</param>
    [TestMethod]
    [DataRow("package")]
    [DataRow("project")]
    public void AdoptReferences_RemovalRebindsUnchangedConsumers(string origin)
    {
        using var fixture = new SessionDependencyFixture();
        var dependency = fixture.AssemblyName + "Dependency";
        var dependencyImage = Image(fixture, dependency);
        var consumer = DelegatingImage(fixture, fixture.AssemblyName, dependencyImage);
        var unrelated = Image(fixture, fixture.AssemblyName + "Unrelated");
        var document = Document(consumer, dependencyImage, unrelated);
        document.References[0].Origin = origin;
        document.References[0].Assets = [document.References[0].Assets[0], document.References[1].Assets[0]];
        document = document with { References = [document.References[0], document.References[2]] };
        using var core = new ReplCore();
        core.AdoptReferences(document);
        var originalConsumer = core.Session.Resolver.LoadedAssemblies.Single(assembly => assembly.GetName().Name == fixture.AssemblyName);
        var untouched = core.Session.Resolver.LoadedAssemblies.Single(assembly => assembly.GetName().Name!.EndsWith("Unrelated"));
        var reduced = document with
        {
            References = [document.References[0] with { Assets = [document.References[0].Assets[0]] }, document.References[1]],
            Assets = document.Assets.Where(asset => asset.Hash != SessionCodec.Hash(dependencyImage)).ToArray(),
        };

        core.AdoptReferences(reduced);
        core.AdoptReferences(reduced);

        var current = core.Session.Resolver.LoadedAssemblies;
        Assert.DoesNotContain(assembly => assembly.GetName().Name == dependency, current);
        Assert.AreSame(untouched, current.Single(assembly => assembly.GetName().Name == untouched.GetName().Name));
        var rebound = current.Single(assembly => assembly.GetName().Name == fixture.AssemblyName);
        Assert.AreNotSame(originalConsumer, rebound);
        var error = Assert.ThrowsExactly<TargetInvocationException>(() =>
            rebound.GetType("DependencySamples.Values")!.GetMethod("Read")!.Invoke(null, null));
        Assert.IsInstanceOfType<FileNotFoundException>(error.InnerException);
        Assert.Contains(dependency, error.InnerException.Message);
    }

    /// <summary>
    /// Removed dependencies used by instructions, committed methods, or executed cells reject adoption without losing the old graph.
    /// </summary>
    /// <param name="use">The retained binding that prevents removal.</param>
    [TestMethod]
    [DataRow("cell")]
    [DataRow("method")]
    [DataRow("activated")]
    public void AdoptReferences_RejectsRemovalInUse(string use)
    {
        using var fixture = new SessionDependencyFixture();
        var image = Image(fixture, fixture.AssemblyName);
        using var core = new ReplCore();
        core.AdoptReferences(Document(image));
        if (use == "method")
        {
            Submit(core, ".method int32 ReadSaved() {");
        }

        Submit(core, "call int32 [" + fixture.AssemblyName + "]DependencySamples.Values::Read()");
        if (use != "cell")
        {
            Submit(core, "ret");
        }

        if (use == "method")
        {
            Submit(core, "}");
        }

        if (use == "activated")
        {
            Submit(core, ".clear");
        }

        var before = core.CaptureSession(new SessionEditor());
        var assembly = core.Session.Resolver.LoadedAssemblies.Single();

        var error = Assert.ThrowsExactly<ReplException>(() => core.AdoptReferences(before with { References = [], Assets = [] }));

        Assert.Contains("changed dependencies are in use by", error.Message);
        Assert.Contains("--reload", error.Message);
        Assert.AreSame(assembly, core.Session.Resolver.LoadedAssemblies.Single());
        Assert.AreSequenceEqual(before.References, core.CaptureSession(new SessionEditor()).References);
        if (use == "method")
        {
            Submit(core, "call ReadSaved");
        }

        if (use == "activated")
        {
            Submit(core, "call int32 [" + fixture.AssemblyName + "]DependencySamples.Values::Read()");
        }

        Submit(core, "ret");
        Assert.Contains(line => line.PlainText.Contains("= 42 : int32", StringComparison.Ordinal), core.Transcript.Lines);
    }

    /// <summary>
    /// Changed transitives rebind unchanged callers and protect callers that have already executed.
    /// </summary>
    /// <param name="activated">Whether the consumer already has an executed runtime binding.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void AdoptReferences_RebindsTransitiveConsumers(bool activated)
    {
        using var fixture = new SessionDependencyFixture();
        var dependency = fixture.AssemblyName + "Dependency";
        var before = Image(fixture, dependency);
        var middle = DelegatingImage(fixture, fixture.AssemblyName + "Middle", before);
        var root = DelegatingImage(fixture, fixture.AssemblyName, middle);
        var document = Document(root, middle, before);
        using var core = new ReplCore();
        core.AdoptReferences(document);
        var call = "call int32 [" + fixture.AssemblyName + "]DependencySamples.Values::Read()";
        if (activated)
        {
            Submit(core, call);
            Submit(core, "ret");
            Submit(core, ".clear");
        }

        fixture.WritePackage(dependency, "2.0.0", 84);
        var after = fixture.PackageImage(dependency, "2.0.0");
        var changed = Document(root, middle, after);
        if (activated)
        {
            var error = Assert.ThrowsExactly<ReplException>(() => core.AdoptReferences(changed));
            Assert.Contains("activated runtime binding " + fixture.AssemblyName, error.Message);
        }
        else
        {
            core.AdoptReferences(changed);
        }

        Submit(core, call);
        Submit(core, "ret");
        Assert.Contains(line => line.PlainText.Contains(activated ? "= 42 : int32" : "= 84 : int32", StringComparison.Ordinal),
            core.Transcript.Lines);
    }

    private static SessionDocument Document(params byte[][] images) => new()
    {
        References = images.Select(image => new SessionReference
        {
            Origin = "package", Request = ReferenceLoadContext.Identity(image).Name!,
            Assets = [new SessionReferenceAsset
            {
                Name = ReferenceLoadContext.Identity(image).FullName, Kind = "managed", Hash = SessionCodec.Hash(image),
            }],
        }).ToArray(),
        Assets = images.Select(image => new SessionAsset { Hash = SessionCodec.Hash(image), Image = image }).ToArray(),
    };

    private static byte[] Image(SessionDependencyFixture fixture, string name)
    {
        fixture.WritePackage(name, "1.0.0", 42);
        return fixture.PackageImage(name, "1.0.0");
    }

    private static byte[] DelegatingImage(SessionDependencyFixture fixture, string name, byte[] dependency)
    {
        using var assembly = AssemblyDefinition.ReadAssembly(new MemoryStream(Image(fixture, name)));
        using var target = AssemblyDefinition.ReadAssembly(new MemoryStream(dependency));
        var method = assembly.MainModule.GetType("DependencySamples.Values").Methods.Single();
        method.Body.Instructions.Clear();
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Call,
            assembly.MainModule.ImportReference(target.MainModule.GetType("DependencySamples.Values").Methods.Single())));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        using var output = new MemoryStream();
        assembly.Write(output);
        return output.ToArray();
    }

    private static void Submit(ReplCore core, string line) =>
        Assert.IsTrue(core.Handle(line).Succeeded, string.Join('\n', core.Transcript.Lines.Select(item => item.PlainText)));
}
