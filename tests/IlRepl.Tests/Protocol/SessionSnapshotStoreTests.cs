using System.Text;
using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Processes;
using IlRepl.Protocol;
using IlRepl.Tests.Engine;

namespace IlRepl.Tests.Protocol;

/// <summary>
/// Checks host-independent saving against actual host saves and private dependency recovery on disk.
/// </summary>
[TestClass]
public sealed class SessionSnapshotStoreTests
{
    /// <summary>
    /// Supplies cancellation to real host and filesystem operations.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Saving after host disposal preserves the same portable document and privately recoverable dependency images.
    /// </summary>
    /// <param name="embed">Whether the shared document embeds the available dependency image.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Write_AgreesWithHostForPortableCachedReferences(bool embed)
    {
        var directory = Directory.CreateTempSubdirectory("ilrepl-offline-save-").FullName;
        try
        {
            var external = Directory.CreateDirectory(Path.Join(directory, "private-machine-location")).FullName;
            var project = Path.Join(external, "Dependency.csproj");
            var assembly = Path.Join(external, "Dependency.dll");
            await File.WriteAllTextAsync(project, "<Project />", TestContext.CancellationToken);
            var image = AssemblyExporter.Write(IlLines.Load(".class public OfflineDependency { }"), "OfflineDependency");
            await File.WriteAllBytesAsync(assembly, image, TestContext.CancellationToken);
            var hash = SessionCodec.Hash(image);
            var document = new SessionDocument
            {
                References = [new SessionReference
                {
                    Identity = "offline-dependency", Origin = "project", Request = project,
                    Assets = [new SessionReferenceAsset { Name = "OfflineDependency", Hash = hash, Path = assembly }],
                }],
                Assets = [new SessionAsset { Hash = hash, Image = image }],
                Editor = new SessionEditor { Lines = ["ldc.i4.s 42"], Caret = 11, Anchor = 11 },
            };

            var path = Path.Join(directory, "shared", "example.ilrepl.json");
            SessionDocument captured;
            byte[] ready;
            await using (var host = await HostProcessEngine.StartAsync(cancellationToken: TestContext.CancellationToken))
            {
                var hydrated = await host.SessionAsync(new SessionRequest
                {
                    Action = new SessionAction { Operation = SessionOperation.Hydrate },
                    Document = document, Editor = document.Editor,
                }, TestContext.CancellationToken);

                Assert.IsTrue(hydrated.Reply.Succeeded, string.Join('\n', hydrated.Reply.Lines.Select(line => line.PlainText)));
                var saved = await host.SessionAsync(new SessionRequest
                {
                    Action = new SessionAction { Operation = SessionOperation.Save, Path = path, Embed = embed },
                    Editor = document.Editor,
                }, TestContext.CancellationToken);

                Assert.IsTrue(saved.Reply.Succeeded);
                captured = saved.Document;
                ready = await File.ReadAllBytesAsync(path, TestContext.CancellationToken);
            }

            var cache = Path.Join(directory, "offline-cache");
            Assert.AreEqual(path, await SessionSnapshotStore.WriteAsync(path, captured, embed, cache, TestContext.CancellationToken));
            var offline = await File.ReadAllBytesAsync(path, TestContext.CancellationToken);
            Assert.AreSequenceEqual(ready, offline);
            Assert.DoesNotContain(external, Encoding.UTF8.GetString(offline));
            var portable = SessionCodec.Read(offline);
            Assert.AreEqual("Dependency.csproj", portable.References.Single().Request);
            Assert.IsNull(portable.References.Single().Assets.Single().Path);
            Assert.AreEqual(embed, portable.Assets.Any(asset => asset.Hash == hash));
            File.Delete(assembly);
            var reopened = await new SessionFileStore(cache).ReadAsync(path, TestContext.CancellationToken);
            Assert.AreEqual(project, reopened.References.Single().Request);
            Assert.AreEqual(assembly, reopened.References.Single().Assets.Single().Path);
            Assert.AreSequenceEqual(image, reopened.Assets.Single(asset => asset.Hash == hash).Image);
            Assert.AreSequenceEqual(document.Editor.Lines, reopened.Editor.Lines);
            Assert.IsEmpty(Directory.GetFiles(directory, "*.tmp", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Invalid or cancelled offline saves leave the previous document and dependency cache unchanged.
    /// </summary>
    [TestMethod]
    public async Task Write_CancellationAndInvalidAssetsPreserveDestination()
    {
        var directory = Directory.CreateTempSubdirectory("ilrepl-offline-cancel-").FullName;
        try
        {
            var path = Path.Join(directory, "example.ilrepl.json");
            var original = SessionCodec.Write(new SessionDocument());
            await File.WriteAllBytesAsync(path, original, TestContext.CancellationToken);
            var cache = Path.Join(directory, "cache");
            var invalid = new SessionDocument { Assets = [new SessionAsset { Hash = new string('0', 64), Image = [1, 2, 3] }] };
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                SessionSnapshotStore.WriteAsync(path, invalid, true, cache, TestContext.CancellationToken));
            using var cancellation = new CancellationTokenSource();
            await cancellation.CancelAsync();
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                SessionSnapshotStore.WriteAsync(path, new SessionDocument(), false, cache, cancellation.Token));
            Assert.AreSequenceEqual(original, await File.ReadAllBytesAsync(path, TestContext.CancellationToken));
            Assert.IsFalse(Directory.Exists(cache));
            Assert.IsEmpty(Directory.GetFiles(directory, "*.tmp", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
