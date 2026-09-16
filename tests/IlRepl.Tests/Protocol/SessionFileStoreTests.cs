using System.Text;
using IlRepl.Host;
using IlRepl.Protocol;

namespace IlRepl.Tests.Protocol;

/// <summary>
/// Desktop session storage preserves source files and uses atomic documents with verified portable dependency assets.
/// </summary>
[TestClass]
public sealed class SessionFileStoreTests
{
    /// <summary>
    /// Supplies cancellation for real isolated filesystem operations.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Saves replace existing documents, resolve relative destination paths, and leave no temporary files.
    /// </summary>
    [TestMethod]
    public async Task Write_ReplacesExistingDocumentAndReturnsAbsolutePath()
    {
        var directory = Directory.CreateTempSubdirectory("ilrepl-store-").FullName;
        try
        {
            var path = Path.Combine(directory, "example.ilrepl.json");
            var relative = Path.GetRelativePath(Environment.CurrentDirectory, path);
            var store = new SessionFileStore(Path.Combine(directory, "cache"));
            await store.WriteAsync(relative, Document("ldc.i4.1"), embed: false, TestContext.CancellationToken);
            var associated = await store.WriteAsync(relative, Document("ldc.i4.2"), embed: true, TestContext.CancellationToken);

            Assert.AreEqual(path, associated);
            var opened = await store.ReadAsync(relative, TestContext.CancellationToken);
            Assert.AreSequenceEqual(["ldc.i4.2"], opened.Editor.Lines);
            Assert.IsEmpty(Directory.GetFiles(directory, "*.tmp", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// First saves create missing parent directories and report the requested destination on filesystem failure.
    /// </summary>
    [TestMethod]
    public async Task Write_CreatesParentsAndNamesDestinationOnFailure()
    {
        var directory = Directory.CreateTempSubdirectory("ilrepl-store-parents-").FullName;
        try
        {
            var path = Path.Combine(directory, "new", "nested", "example.ilrepl.json");
            var store = new SessionFileStore(Path.Combine(directory, "cache"));
            Assert.AreEqual(path, await store.WriteAsync(path, Document("ldc.i4.1"), false, TestContext.CancellationToken));
            Assert.AreSequenceEqual(["ldc.i4.1"], (await store.ReadAsync(path, TestContext.CancellationToken)).Editor.Lines);
            var impossible = Path.Combine(path, "child.ilrepl.json");
            var error = await Assert.ThrowsExactlyAsync<IOException>(() =>
                store.WriteAsync(impossible, Document("ldc.i4.2"), false, TestContext.CancellationToken));
            Assert.StartsWith("could not save session '" + impossible + "':", error.Message);
            Assert.IsEmpty(Directory.GetFiles(directory, "*.tmp", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Concurrent readers observe complete old or new documents while repeated atomic replacements are in progress.
    /// </summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Write_ConcurrentReadersNeverObservePartialDocuments()
    {
        var directory = Directory.CreateTempSubdirectory("ilrepl-store-atomic-").FullName;
        try
        {
            var path = Path.Combine(directory, "example.ilrepl.json");
            var store = new SessionFileStore(Path.Combine(directory, "cache"));
            var first = Document(new string('a', 64 * 1024));
            var second = Document(new string('b', 64 * 1024));
            var token = TestContext.CancellationToken;
            await store.WriteAsync(path, first, false, token);
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var reader = ObserveAsync();
            await ready.Task.WaitAsync(token);
            try
            {
                for (var index = 0; index < 12; index++)
                {
                    await store.WriteAsync(path, (index & 1) == 0 ? second : first, false, token);
                }
            }
            finally
            {
                finished.TrySetResult();
                await reader;
            }

            Assert.AreSequenceEqual(first.Editor.Lines, (await store.ReadAsync(path, token)).Editor.Lines);
            Assert.IsEmpty(Directory.GetFiles(directory, "*.tmp", SearchOption.AllDirectories));

            async Task ObserveAsync()
            {
                try
                {
                    do
                    {
                        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read,
                            FileShare.ReadWrite | FileShare.Delete, 8192, useAsync: true);
                        using var bytes = new MemoryStream();
                        await input.CopyToAsync(bytes, token);
                        var source = Assert.ContainsSingle(SessionCodec.Read(bytes.ToArray()).Editor.Lines);
                        Assert.IsTrue(source == first.Editor.Lines[0] || source == second.Editor.Lines[0],
                            "A reader observed content from an incomplete or mixed document.");
                        ready.TrySetResult();
                    }
                    while (!finished.Task.IsCompleted);
                }
                finally
                {
                    ready.TrySetResult();
                }
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Saving over an open document preserves its reader and publishes the new revision, including long Unicode paths.
    /// </summary>
    /// <param name="name">The destination filename passed through the platform's rename operation.</param>
    /// <param name="longPath">Whether the destination exceeds the legacy Windows path limit.</param>
    [TestMethod]
    [DataRow("example.ilrepl.json", false)]
    [DataRow("session λ 😀.ilrepl.json", true)]
    public async Task Write_ReplacesDocumentWhilePreviousReaderRemainsOpen(string name, bool longPath)
    {
        var directory = Directory.CreateTempSubdirectory("ilrepl-store-reader-").FullName;
        try
        {
            var parent = longPath ? Path.Combine(directory, new string('a', 100), new string('b', 100), new string('c', 100)) : directory;
            var path = Path.Combine(parent, name);
            var store = new SessionFileStore(Path.Combine(directory, "cache"));
            var token = TestContext.CancellationToken;
            await store.WriteAsync(path, Document("ldc.i4.1"), false, token);
            await using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);

            await store.WriteAsync(path, Document("ldc.i4.2"), false, token);
            await store.WriteAsync(path, Document("ldc.i4.3"), false, token);

            Assert.AreSequenceEqual(["ldc.i4.3"], (await store.ReadAsync(path, token)).Editor.Lines);
            using var previous = new MemoryStream();
            await reader.CopyToAsync(previous, token);
            Assert.AreSequenceEqual(["ldc.i4.1"], SessionCodec.Read(previous.ToArray()).Editor.Lines);
            Assert.IsEmpty(Directory.GetFiles(directory, "*.tmp", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Save As rewrites project and assembly locators relative to the new file while leaving sources and snapshots unchanged.
    /// </summary>
    [TestMethod]
    public async Task SaveAs_RebasesDependencyPathsWithoutChangingSourceFiles()
    {
        var directory = Directory.CreateTempSubdirectory("ilrepl-store-paths-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, ".git"));
            var firstDirectory = Directory.CreateDirectory(Path.Combine(directory, "first")).FullName;
            var secondDirectory = Directory.CreateDirectory(Path.Combine(directory, "second folder λ")).FullName;
            var assetPath = Path.Combine(directory, "dependency.dll");
            var projectPath = Path.Combine(directory, "fixture.csproj");
            byte[] image = [3, 1, 4, 1, 5];
            await File.WriteAllBytesAsync(assetPath, image, TestContext.CancellationToken);
            await File.WriteAllTextAsync(projectPath, "<Project />", TestContext.CancellationToken);
            var document = Referencing(assetPath, image) with
            {
                References =
                [
                    .. Referencing(assetPath, image).References,
                    new() { Identity = "project", Origin = "project", Request = projectPath },
                    new() { Identity = "package", Origin = "package", Request = "Fixture.Package", Version = "1.2.3" },
                    new() { Identity = "framework", Request = "System.Net.Http" },
                ],
            };
            var original = SessionCodec.Write(document);
            var store = new SessionFileStore(Path.Combine(directory, "cache"));
            var firstPath = Path.Combine(firstDirectory, "first.ilrepl.json");
            var secondPath = Path.Combine(secondDirectory, "second.ilrepl.json");
            await store.WriteAsync(firstPath, document, embed: false, TestContext.CancellationToken);
            var opened = await store.ReadAsync(firstPath, TestContext.CancellationToken);
            Assert.AreEqual(assetPath, opened.References[0].Request);
            Assert.AreEqual(assetPath, opened.References[0].Assets[0].Path);
            Assert.AreEqual(projectPath, opened.References[1].Request);

            await store.WriteAsync(secondPath, opened, embed: false, TestContext.CancellationToken);
            var saved = SessionCodec.Read(await File.ReadAllBytesAsync(secondPath, TestContext.CancellationToken));
            Assert.AreEqual(Path.GetRelativePath(secondDirectory, assetPath).Replace(Path.DirectorySeparatorChar, '/'),
                saved.References[0].Request);
            Assert.AreEqual(Path.GetRelativePath(secondDirectory, assetPath).Replace(Path.DirectorySeparatorChar, '/'),
                saved.References[0].Assets[0].Path);
            Assert.AreEqual(Path.GetRelativePath(secondDirectory, projectPath).Replace(Path.DirectorySeparatorChar, '/'),
                saved.References[1].Request);
            Assert.AreEqual("Fixture.Package", saved.References[2].Request);
            Assert.AreEqual("System.Net.Http", saved.References[3].Request);
            Assert.AreEqual("System.Net.Http", opened.References[3].Request);
            Assert.AreSequenceEqual(image, await File.ReadAllBytesAsync(assetPath, TestContext.CancellationToken));
            Assert.AreEqual("<Project />", await File.ReadAllTextAsync(projectPath, TestContext.CancellationToken));
            Assert.AreSequenceEqual(original, SessionCodec.Write(document));
            Assert.IsEmpty(Directory.GetFiles(directory, "*.tmp", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// A relative load request is resolved from the caller's directory before the first save rebases it to the document.
    /// </summary>
    [TestMethod]
    public async Task Write_RebasesRelativeLoadRequestsForTheFirstSave()
    {
        var directory = Directory.CreateTempSubdirectory("ilrepl-store-relative-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, ".git"));
            var documentDirectory = Directory.CreateDirectory(Path.Combine(directory, "documents")).FullName;
            var source = Path.Combine(directory, "source.dll");
            byte[] image = [1, 2, 3];
            await File.WriteAllBytesAsync(source, image, TestContext.CancellationToken);
            var relative = Path.GetRelativePath(Environment.CurrentDirectory, source);
            var document = Referencing(source, image);
            document = document with { References = [document.References[0] with { Request = relative }] };
            var path = Path.Combine(documentDirectory, "example.ilrepl.json");
            var store = new SessionFileStore(Path.Combine(directory, "cache"));

            await store.WriteAsync(path, document, embed: false, TestContext.CancellationToken);
            var saved = SessionCodec.Read(await File.ReadAllBytesAsync(path, TestContext.CancellationToken));
            Assert.AreEqual(Path.GetRelativePath(documentDirectory, source).Replace(Path.DirectorySeparatorChar, '/'),
                saved.References[0].Request);
            Assert.AreEqual(source, (await store.ReadAsync(path, TestContext.CancellationToken)).References[0].Request);
            Assert.AreEqual(relative, document.References[0].Request);
            Assert.AreSequenceEqual(image, await File.ReadAllBytesAsync(source, TestContext.CancellationToken));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Session files written with Windows relative separators locate neighboring assets on every desktop platform.
    /// </summary>
    [TestMethod]
    public async Task Read_ResolvesPortableLocatorsWithEitherSeparator()
    {
        var directory = Directory.CreateTempSubdirectory("ilrepl-store-portable-").FullName;
        try
        {
            byte[] image = [1, 2, 3];
            var binaries = Directory.CreateDirectory(Path.Combine(directory, "binaries")).FullName;
            var source = Path.Combine(binaries, "source.dll");
            await File.WriteAllBytesAsync(source, image, TestContext.CancellationToken);
            var document = Referencing("binaries\\source.dll", image) with { Assets = [] };
            var path = Path.Combine(directory, "example.ilrepl.json");
            await File.WriteAllBytesAsync(path, SessionCodec.Write(document), TestContext.CancellationToken);
            var store = new SessionFileStore(Path.Combine(directory, "cache"));

            var opened = await store.ReadAsync(path, TestContext.CancellationToken);

            Assert.AreEqual(source, opened.References.Single().Request);
            Assert.AreEqual(source, opened.References.Single().Assets.Single().Path);
            Assert.AreSequenceEqual(image, opened.Assets.Single().Image);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Ordinary saves retain immutable baselines while portable saves retain every available image.
    /// </summary>
    /// <param name="embed">Whether this save embeds all available dependencies.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Write_PreservesBaselinesAndCachesAllAvailableImages(bool embed)
    {
        var directory = Directory.CreateTempSubdirectory("ilrepl-store-assets-").FullName;
        try
        {
            byte[] baseline = [2, 4, 6];
            byte[] dependency = [1, 3, 5];
            var baselineHash = SessionCodec.Hash(baseline);
            var dependencyHash = SessionCodec.Hash(dependency);
            var document = Document("call Copy") with
            {
                References =
                [
                    new() { Identity = "original", Origin = "baseline", Request = "original-fingerprint",
                        Assets = [new() { Name = "Baseline", Hash = baselineHash }] },
                    new() { Identity = "dependency", Request = Path.Combine(directory, "removed.dll"),
                        Assets = [new() { Name = "Dependency", Hash = dependencyHash }] },
                ],
                Assets = [new() { Hash = baselineHash, Image = baseline }, new() { Hash = dependencyHash, Image = dependency }],
            };
            var cache = Path.Combine(directory, "cache");
            var store = new SessionFileStore(cache);
            var path = Path.Combine(directory, "example.ilrepl.json");
            await store.WriteAsync(path, document, embed, TestContext.CancellationToken);

            var serialized = SessionCodec.Read(await File.ReadAllBytesAsync(path, TestContext.CancellationToken));
            Assert.HasCount(embed ? 2 : 1, serialized.Assets);
            Assert.Contains(asset => asset.Hash == baselineHash && asset.Image.SequenceEqual(baseline), serialized.Assets);
            Assert.AreSequenceEqual(dependency,
                await File.ReadAllBytesAsync(Path.Combine(cache, dependencyHash), TestContext.CancellationToken));
            Assert.AreSequenceEqual(baseline,
                await File.ReadAllBytesAsync(Path.Combine(cache, baselineHash), TestContext.CancellationToken));
            var reopened = await store.ReadAsync(path, TestContext.CancellationToken);
            Assert.HasCount(2, reopened.Assets);
            Assert.Contains(asset => asset.Hash == dependencyHash && asset.Image.SequenceEqual(dependency), reopened.Assets);

            Directory.Delete(cache, recursive: true);
            var withoutCache = await store.ReadAsync(path, TestContext.CancellationToken);
            Assert.HasCount(embed ? 2 : 1, withoutCache.Assets);
            Assert.Contains(asset => asset.Hash == baselineHash, withoutCache.Assets);
            Assert.AreSequenceEqual(["call Copy"], withoutCache.Editor.Lines);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Embedded images remain authoritative when both the source file and the local cache contain different bytes.
    /// </summary>
    [TestMethod]
    public async Task Read_UsesVerifiedEmbeddedImageBeforeChangedExternalFiles()
    {
        var directory = Directory.CreateTempSubdirectory("ilrepl-store-embedded-").FullName;
        try
        {
            byte[] original = [1, 2, 3];
            byte[] changed = [4, 5, 6];
            var source = Path.Combine(directory, "source.dll");
            var cache = Path.Combine(directory, "cache");
            var path = Path.Combine(directory, "example.ilrepl.json");
            var store = new SessionFileStore(cache);
            await store.WriteAsync(path, Referencing(source, original), embed: true, TestContext.CancellationToken);
            await File.WriteAllBytesAsync(source, changed, TestContext.CancellationToken);
            await File.WriteAllBytesAsync(Path.Combine(cache, SessionCodec.Hash(original)), changed, TestContext.CancellationToken);

            var opened = await store.ReadAsync(path, TestContext.CancellationToken);
            var asset = Assert.ContainsSingle(opened.Assets);
            Assert.AreEqual(SessionCodec.Hash(original), asset.Hash);
            Assert.AreSequenceEqual(original, asset.Image);
            Assert.AreSequenceEqual(changed, await File.ReadAllBytesAsync(source, TestContext.CancellationToken));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// A changed project or assembly output does not silently replace the experiment's cached content identity.
    /// </summary>
    /// <param name="cached">Whether the original bytes are still available in the isolated cache.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Read_RejectsChangedReferenceBytesAndUsesOriginalCacheWhenAvailable(bool cached)
    {
        var directory = Directory.CreateTempSubdirectory("ilrepl-store-identity-").FullName;
        try
        {
            byte[] original = [1, 2, 3];
            var source = Path.Combine(directory, "source.dll");
            var cache = Path.Combine(directory, "cache");
            var path = Path.Combine(directory, "example.ilrepl.json");
            var store = new SessionFileStore(cache);
            await store.WriteAsync(path, Referencing(source, original), embed: false, TestContext.CancellationToken);
            if (!cached)
            {
                Directory.Delete(cache, recursive: true);
            }

            await File.WriteAllBytesAsync(source, [9, 8, 7], TestContext.CancellationToken);
            var opened = await store.ReadAsync(path, TestContext.CancellationToken);
            Assert.AreEqual(SessionCodec.Hash(original), opened.References[0].Assets[0].Hash);
            Assert.HasCount(cached ? 1 : 0, opened.Assets);
            if (cached)
            {
                Assert.AreSequenceEqual(original, opened.Assets[0].Image);
            }

            Assert.AreSequenceEqual(["ldc.i4.s 42"], opened.Editor.Lines);
            Assert.AreSequenceEqual(new byte[] { 9, 8, 7 }, await File.ReadAllBytesAsync(source, TestContext.CancellationToken));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Invalid cached bytes cannot prevent a matching original dependency file from recovering the document.
    /// </summary>
    /// <param name="oversized">Whether the invalid cache entry also exceeds the asset size limit.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Read_FallsBackFromCorruptCacheToVerifiedSource(bool oversized)
    {
        var directory = Directory.CreateTempSubdirectory("ilrepl-store-cache-").FullName;
        try
        {
            byte[] image = [5, 4, 3, 2, 1];
            var source = Path.Combine(directory, "source.dll");
            var cache = Path.Combine(directory, "cache");
            var path = Path.Combine(directory, "example.ilrepl.json");
            var store = new SessionFileStore(cache);
            await File.WriteAllBytesAsync(source, image, TestContext.CancellationToken);
            await store.WriteAsync(path, Referencing(source, image), embed: false, TestContext.CancellationToken);
            var cachePath = Path.Combine(cache, SessionCodec.Hash(image));
            await using (var stream = new FileStream(cachePath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.SetLength(oversized ? SessionCodec.FileLimit + 1L : 1L);
            }

            var opened = await store.ReadAsync(path, TestContext.CancellationToken);
            var asset = Assert.ContainsSingle(opened.Assets);
            Assert.AreEqual(SessionCodec.Hash(image), asset.Hash);
            Assert.AreSequenceEqual(image, asset.Image);
            Assert.AreSequenceEqual(image, await File.ReadAllBytesAsync(source, TestContext.CancellationToken));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// A valid document with unavailable dependencies opens with its editable source and unresolved identities intact.
    /// </summary>
    [TestMethod]
    public async Task Read_PreservesSourceAndManifestWhenDependencyIsMissing()
    {
        var directory = Directory.CreateTempSubdirectory("ilrepl-store-missing-").FullName;
        try
        {
            var path = Path.Combine(directory, "example.ilrepl.json");
            var missing = Path.Combine(directory, "missing.dll");
            var document = Referencing(missing, [1]) with { Assets = [] };
            var store = new SessionFileStore(Path.Combine(directory, "cache"));
            await store.WriteAsync(path, document, embed: false, TestContext.CancellationToken);

            var opened = await store.ReadAsync(path, TestContext.CancellationToken);
            Assert.IsEmpty(opened.Assets);
            Assert.AreSequenceEqual(["ldc.i4.s 42"], opened.Editor.Lines);
            Assert.AreEqual(missing, opened.References[0].Request);
            Assert.AreEqual(missing, opened.References[0].Assets[0].Path);
            Assert.AreEqual(SessionCodec.Hash([1]), opened.References[0].Assets[0].Hash);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// A malformed document or mismatched embedded image is refused without rewriting the source file.
    /// </summary>
    /// <param name="corruptAsset">Whether valid JSON contains an invalid embedded content hash.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Read_RejectsMalformedAndCorruptDocumentsWithoutChangingFiles(bool corruptAsset)
    {
        var directory = Directory.CreateTempSubdirectory("ilrepl-store-invalid-").FullName;
        try
        {
            var path = Path.Combine(directory, "example.ilrepl.json");
            var content = corruptAsset
                ? "{\"format\":\"ilrepl-session\",\"version\":1,\"assets\":[{\"hash\":\"" + SessionCodec.Hash([1])
                    + "\",\"image\":\"Ag==\"}]}"
                : "{ invalid JSON";
            await File.WriteAllTextAsync(path, content, TestContext.CancellationToken);
            var store = new SessionFileStore(Path.Combine(directory, "cache"));

            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => store.ReadAsync(path, TestContext.CancellationToken));
            Assert.AreEqual(content, await File.ReadAllTextAsync(path, TestContext.CancellationToken));
            Assert.IsFalse(Directory.Exists(Path.Combine(directory, "cache")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Missing session files produce the filesystem error used by startup exit handling.
    /// </summary>
    [TestMethod]
    public async Task Read_MissingSessionReportsFileNotFound()
    {
        var directory = Directory.CreateTempSubdirectory("ilrepl-store-absent-").FullName;
        try
        {
            var store = new SessionFileStore(Path.Combine(directory, "cache"));
            var path = Path.Combine(directory, "missing.ilrepl.json");
            var error = await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => store.ReadAsync(path, TestContext.CancellationToken));
            Assert.AreEqual(path, error.FileName);
            Assert.AreEqual("session file does not exist: " + path, error.Message);
            Assert.IsEmpty(Directory.GetFiles(directory, "*", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Cancellation preserves the previously associated document and removes any provisional sibling file.
    /// </summary>
    [TestMethod]
    public async Task Write_CanceledOperationPreservesDestinationAndRemovesTemporaryFiles()
    {
        var directory = Directory.CreateTempSubdirectory("ilrepl-store-cancel-").FullName;
        try
        {
            var path = Path.Combine(directory, "example.ilrepl.json");
            var original = SessionCodec.Write(Document("ldc.i4.1"));
            await File.WriteAllBytesAsync(path, original, TestContext.CancellationToken);
            var store = new SessionFileStore(Path.Combine(directory, "cache"));
            using var cancellation = new CancellationTokenSource();
            await cancellation.CancelAsync();

            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                store.WriteAsync(path, Document("ldc.i4.2"), embed: false, cancellation.Token));
            Assert.AreSequenceEqual(original, await File.ReadAllBytesAsync(path, TestContext.CancellationToken));
            Assert.IsEmpty(Directory.GetFiles(directory, "*.tmp", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// A real atomic cache replacement failure leaves the previous session document and no temporary files.
    /// </summary>
    [TestMethod]
    public async Task Write_FailedCacheReplacementPreservesDestinationAndCleansTemporaryFiles()
    {
        var directory = Directory.CreateTempSubdirectory("ilrepl-store-failure-").FullName;
        try
        {
            var path = Path.Combine(directory, "example.ilrepl.json");
            var original = SessionCodec.Write(Document("ldc.i4.1"));
            await File.WriteAllBytesAsync(path, original, TestContext.CancellationToken);
            byte[] image = [3, 2, 1];
            var cache = Directory.CreateDirectory(Path.Combine(directory, "cache")).FullName;
            var collision = Directory.CreateDirectory(Path.Combine(cache, SessionCodec.Hash(image))).FullName;
            await File.WriteAllTextAsync(Path.Combine(collision, "keep"), "original", TestContext.CancellationToken);
            var store = new SessionFileStore(cache);

            await AssertWriteFailureAsync(() =>
                store.WriteAsync(path, Referencing(Path.Combine(directory, "source.dll"), image), false, TestContext.CancellationToken));
            Assert.AreSequenceEqual(original, await File.ReadAllBytesAsync(path, TestContext.CancellationToken));
            Assert.AreEqual("original", await File.ReadAllTextAsync(Path.Combine(collision, "keep"), TestContext.CancellationToken));
            Assert.IsEmpty(Directory.GetFiles(directory, "*.tmp", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// A failed final rename removes its temporary file and preserves the existing destination directory.
    /// </summary>
    [TestMethod]
    public async Task Write_FailedDestinationReplacementCleansTemporaryFiles()
    {
        var directory = Directory.CreateTempSubdirectory("ilrepl-store-rename-").FullName;
        try
        {
            var path = Directory.CreateDirectory(Path.Combine(directory, "example.ilrepl.json")).FullName;
            var marker = Path.Combine(path, "keep");
            await File.WriteAllTextAsync(marker, "original", TestContext.CancellationToken);
            var store = new SessionFileStore(Path.Combine(directory, "cache"));

            await AssertWriteFailureAsync(() => store.WriteAsync(path, Document("ldc.i4.2"), false, TestContext.CancellationToken));
            Assert.AreEqual("original", await File.ReadAllTextAsync(marker, TestContext.CancellationToken));
            Assert.IsEmpty(Directory.GetFiles(directory, "*.tmp", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Invalid snapshots are rejected before replacing the file or populating an asset cache.
    /// </summary>
    [TestMethod]
    public async Task Write_InvalidDocumentPreservesDestination()
    {
        var directory = Directory.CreateTempSubdirectory("ilrepl-store-validation-").FullName;
        try
        {
            var path = Path.Combine(directory, "example.ilrepl.json");
            var original = SessionCodec.Write(Document("ldc.i4.1"));
            await File.WriteAllBytesAsync(path, original, TestContext.CancellationToken);
            var cache = Path.Combine(directory, "cache");
            var store = new SessionFileStore(cache);

            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                store.WriteAsync(path, Document("ldc.i4.2") with { Version = 2 }, false, TestContext.CancellationToken));
            Assert.AreSequenceEqual(original, await File.ReadAllBytesAsync(path, TestContext.CancellationToken));
            Assert.IsFalse(Directory.Exists(cache));
            Assert.IsEmpty(Directory.GetFiles(directory, "*.tmp", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Session file reads accept the inclusive byte limit and reject the next byte before decoding JSON.
    /// </summary>
    [TestMethod]
    public async Task Read_EnforcesFileLimitAtBoundary()
    {
        var directory = Directory.CreateTempSubdirectory("ilrepl-store-limit-").FullName;
        try
        {
            var path = Path.Combine(directory, "example.ilrepl.json");
            var bytes = new byte[SessionCodec.FileLimit];
            bytes.AsSpan().Fill((byte)' ');
            SessionCodec.Write(Document("ldc.i4.s 42")).CopyTo(bytes, 0);
            await File.WriteAllBytesAsync(path, bytes, TestContext.CancellationToken);
            var store = new SessionFileStore(Path.Combine(directory, "cache"));
            Assert.AreSequenceEqual(["ldc.i4.s 42"], (await store.ReadAsync(path, TestContext.CancellationToken)).Editor.Lines);

            await using (var output = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None))
            {
                output.WriteByte((byte)' ');
            }

            var error = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => store.ReadAsync(path, TestContext.CancellationToken));
            Assert.Contains("64 MiB", error.Message);
            Assert.AreEqual(SessionCodec.FileLimit + 1L, new FileInfo(path).Length);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Projects outside the shared repository retain private local recovery hints without publishing traversal-heavy machine paths.
    /// </summary>
    [TestMethod]
    public async Task SaveAs_KeepsExternalProjectLocatorsPrivateAndRetainsLocalRecovery()
    {
        var directory = Directory.CreateTempSubdirectory("ilrepl-store-external-").FullName;
        try
        {
            var sourceDirectory = Directory.CreateDirectory(Path.Combine(directory, "outside")).FullName;
            var project = Path.Combine(sourceDirectory, "example.csproj");
            var source = Path.Combine(sourceDirectory, "example.dll");
            byte[] image = [1, 2, 3];
            await File.WriteAllTextAsync(project, "<Project />", TestContext.CancellationToken);
            await File.WriteAllBytesAsync(source, image, TestContext.CancellationToken);
            var document = Referencing(source, image);
            document = document with { References = [document.References[0] with { Origin = "project", Request = project }] };
            var store = new SessionFileStore(Path.Combine(directory, "cache"));
            var first = Path.Combine(directory, "first", "example.ilrepl.json");
            var second = Path.Combine(directory, "second", "example.ilrepl.json");
            await store.WriteAsync(first, document, false, TestContext.CancellationToken);
            var opened = await store.ReadAsync(first, TestContext.CancellationToken);
            Assert.AreEqual(project, opened.References.Single().Request);
            Assert.AreEqual(source, opened.References.Single().Assets.Single().Path);
            await store.WriteAsync(second, opened, false, TestContext.CancellationToken);

            foreach (var path in new[] { first, second })
            {
                var bytes = await File.ReadAllBytesAsync(path, TestContext.CancellationToken);
                var saved = SessionCodec.Read(bytes);
                Assert.AreEqual("example.csproj", saved.References.Single().Request);
                Assert.IsNull(saved.References.Single().Assets.Single().Path);
                Assert.DoesNotContain(sourceDirectory, Encoding.UTF8.GetString(bytes));
                Assert.AreEqual(project, (await store.ReadAsync(path, TestContext.CancellationToken)).References.Single().Request);
            }

            var elsewhere = new SessionFileStore(Path.Combine(directory, "empty-cache"));
            var portable = await elsewhere.ReadAsync(second, TestContext.CancellationToken);
            Assert.AreEqual(Path.Combine(Path.GetDirectoryName(second)!, "example.csproj"), portable.References.Single().Request);
            Assert.IsEmpty(portable.Assets);
            Assert.AreSequenceEqual(document.Editor.Lines, portable.Editor.Lines);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Moving a repository preserves sibling project locators and prefers the moved files over private hints to their old locations.
    /// </summary>
    [TestMethod]
    public async Task Read_MovedRepositoryUsesPortableSiblingLocators()
    {
        var directory = Directory.CreateTempSubdirectory("ilrepl-store-moved-").FullName;
        try
        {
            var repository = Directory.CreateDirectory(Path.Combine(directory, "repository")).FullName;
            Directory.CreateDirectory(Path.Combine(repository, ".git"));
            var sourceDirectory = Directory.CreateDirectory(Path.Combine(repository, "source")).FullName;
            var project = Path.Combine(sourceDirectory, "example.csproj");
            var source = Path.Combine(sourceDirectory, "example.dll");
            byte[] image = [1, 2, 3];
            await File.WriteAllTextAsync(project, "<Project />", TestContext.CancellationToken);
            await File.WriteAllBytesAsync(source, image, TestContext.CancellationToken);
            var document = Referencing(source, image);
            document = document with { References = [document.References[0] with { Origin = "project", Request = project }] };
            var path = Path.Combine(repository, "sessions", "example.ilrepl.json");
            var store = new SessionFileStore(Path.Combine(directory, "cache"));
            await store.WriteAsync(path, document, false, TestContext.CancellationToken);
            var saved = SessionCodec.Read(await File.ReadAllBytesAsync(path, TestContext.CancellationToken));
            Assert.AreEqual("../source/example.csproj", saved.References.Single().Request);
            Assert.AreEqual("../source/example.dll", saved.References.Single().Assets.Single().Path);
            var moved = Path.Combine(directory, "moved");
            Directory.Move(repository, moved);

            var opened = await store.ReadAsync(Path.Combine(moved, "sessions", "example.ilrepl.json"), TestContext.CancellationToken);

            Assert.AreEqual(Path.Combine(moved, "source", "example.csproj"), opened.References.Single().Request);
            Assert.AreEqual(Path.Combine(moved, "source", "example.dll"), opened.References.Single().Assets.Single().Path);
            Assert.AreSequenceEqual(image, opened.Assets.Single().Image);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Package-relative locations reject traversal, absolute paths and URLs before writing any session or cache files.
    /// </summary>
    /// <param name="packagePath">The malformed package locator.</param>
    [TestMethod]
    [DataRow("../outside.dll")]
    [DataRow("/outside.dll")]
    [DataRow("lib/../../outside.dll")]
    [DataRow("C:/outside.dll")]
    [DataRow("https://example.test/asset.dll")]
    [DataRow("lib\\outside.dll")]
    public async Task Write_RejectsEscapingPackageLocatorsBeforeFileAccess(string packagePath)
    {
        var directory = Directory.CreateTempSubdirectory("ilrepl-store-package-path-").FullName;
        try
        {
            var document = Referencing("Safe.Package", [1, 2, 3]);
            document = document with { References = [document.References[0] with
            {
                Origin = "package", Version = "1.0.0",
                Assets = [document.References[0].Assets[0] with { PackagePath = packagePath }],
            }] };
            var store = new SessionFileStore(Path.Combine(directory, "cache"));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                store.WriteAsync(Path.Combine(directory, "invalid.ilrepl.json"), document, false, TestContext.CancellationToken));
            Assert.IsEmpty(Directory.EnumerateFileSystemEntries(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static SessionDocument Document(string source) => new()
    {
        Editor = new() { Lines = [source], Caret = source.Length, Anchor = source.Length },
    };

    private static SessionDocument Referencing(string path, byte[] image)
    {
        var hash = SessionCodec.Hash(image);
        return Document("ldc.i4.s 42") with
        {
            References = [new() { Identity = "assembly", Request = path, Assets = [new() { Name = "Fixture", Hash = hash, Path = path }] }],
            Assets = [new() { Hash = hash, Image = image }],
        };
    }

    private static async Task AssertWriteFailureAsync(Func<Task> write)
    {
        Exception? failure = null;
        try
        {
            await write();
        }
        catch (IOException exception)
        {
            failure = exception;
        }
        catch (UnauthorizedAccessException exception)
        {
            // Windows can classify replacing an existing directory as access denied.
            failure = exception;
        }

        Assert.IsNotNull(failure, "Replacing a nonempty directory with a file must fail.");
    }
}
