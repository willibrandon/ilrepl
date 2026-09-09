using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Runtime.Loader;
using IlRepl.Engine.Binding;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Deferred completion details preserve reflection identities and recover after cancelled warmup.
/// </summary>
[TestClass]
public sealed class TypeIndexWarmupTests
{
    /// <summary>
    /// Supplies cancellation for metadata warmup.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Concurrent detail warmups retain every runtime type's identity, visibility, nesting, and generic arity.
    /// </summary>
    [TestMethod]
    public async Task WarmEntries_AgreeWithRuntimeDefinitions()
    {
        var context = new AssemblyLoadContext("index-details", isCollectible: true);
        try
        {
            using var image = File.OpenRead(SampleHost.Samples.GreeterDll);
            var assembly = context.LoadFromStream(image);
            var source = AssemblySymbolSource.For(assembly)!;
            using var lease = source.Lease();
            await source.WarmIndexAsync(TestContext.CancellationToken);
            var index = source.Index;
            await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => index.WarmEntriesAsync(TestContext.CancellationToken).AsTask()));
            var types = assembly.GetTypes();
            Assert.HasCount(types.Length, index.Entries);
            foreach (var type in types)
            {
                var handle = (TypeDefinitionHandle)MetadataTokens.EntityHandle(type.MetadataToken);
                var entry = index.EntryOf(handle);
                Assert.IsNotNull(entry);
                Assert.AreEqual(type.MetadataToken, entry.Definition.Token);
                Assert.AreEqual(SymbolRenderer.IlPath(RuntimeSymbolImporter.Import(type)), entry.IlPath);
                Assert.AreEqual(type.IsVisible, entry.IsVisible);
                Assert.AreEqual(type.IsNested, entry.IsNested);
                Assert.AreEqual(type.GetGenericArguments().Length, entry.Arity);
                Assert.Contains(entry, index.Entries);
            }
        }
        finally
        {
            context.Unload();
        }
    }

    /// <summary>
    /// Cancelling detail warmup preserves name lookup and a later request publishes the complete detail list.
    /// </summary>
    [TestMethod]
    public async Task WarmEntries_CancelledRequest_DoesNotHideDefinitions()
    {
        var context = new AssemblyLoadContext("index-cancellation", isCollectible: true);
        try
        {
            using var image = File.OpenRead(SampleHost.Samples.GreeterDll);
            var assembly = context.LoadFromStream(image);
            var source = AssemblySymbolSource.For(assembly)!;
            using var lease = source.Lease();
            await source.WarmIndexAsync(TestContext.CancellationToken);
            var index = source.Index;
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => index.WarmEntriesAsync(new CancellationToken(true)).AsTask());
            Assert.IsTrue(index.TryGetDefinition("Greeter", "Hello", out var handle));
            Assert.AreEqual("Greeter.Hello", index.EntryOf(handle)!.IlPath);
            await index.WarmEntriesAsync(TestContext.CancellationToken);
            Assert.HasCount(assembly.GetTypes().Length, index.Entries);
            Assert.Contains(entry => entry.IlPath == "Greeter.Outer/Inner", index.Entries);
        }
        finally
        {
            context.Unload();
        }
    }
}
