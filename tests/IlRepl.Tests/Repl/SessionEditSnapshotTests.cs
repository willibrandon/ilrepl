using System.Reflection;
using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Protocol;
using IlRepl.Repl;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Instruction = Mono.Cecil.Cil.Instruction;
using MethodAttributes = Mono.Cecil.MethodAttributes;
using TypeAttributes = Mono.Cecil.TypeAttributes;

namespace IlRepl.Tests.Repl;

/// <summary>
/// Frozen edit originals retain the captured implementation and private helpers after their dependency is replaced.
/// </summary>
[TestClass]
public sealed class SessionEditSnapshotTests
{
    /// <summary>
    /// Supplies cancellation for real host storage, reload, and comparison operations.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Saved originals and revisions retain their closed method or declaring-type instantiation.
    /// </summary>
    /// <param name="genericOwner">Whether the closed argument belongs to the declaring type.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Reopen_PreservesClosedGenericOriginalAndRevision(bool genericOwner)
    {
        using var initial = new ReplCore();
        string[] declaration = genericOwner
            ? [".class public Choice`1<T> {", ".method public static !0 Pick(!0 first, !0 second) {", "ldarg.0", "ret", "}", "}"]
            : [".class public Choice {", ".method public static !!0 Pick<T>(!!0 first, !!0 second) {", "ldarg.0", "ret", "}", "}"];
        foreach (var line in declaration) Assert.IsTrue(initial.Handle(line).Succeeded, Transcript(initial));
        var reference = genericOwner ? "!0 Choice`1<int32>::Pick(!0, !0)" : "!!0 Choice::Pick<int32>(!!0, !!0)";
        var prepared = initial.Handle(".edit " + reference + " as Changed");
        Assert.IsNotNull(prepared.EditDocument, Transcript(initial));
        foreach (var line in prepared.EditDocument.Source.Replace("ldarg.0", "ldarg.1", StringComparison.Ordinal).Split('\n'))
        {
            Assert.IsTrue(initial.Handle(line).Succeeded, Transcript(initial));
        }

        var saved = SessionCodec.Read(SessionCodec.Write(initial.CaptureSession(new SessionEditor())));
        using var reopened = new ReplCore();
        Assert.IsEmpty(reopened.ReopenSession(saved));
        var edit = Assert.ContainsSingle(reopened.Session.Edits);

        Assert.IsFalse(edit.OriginalMethod.ContainsGenericParameters);
        Assert.IsFalse(edit.Method!.ContainsGenericParameters);
        Assert.AreEqual(41, edit.OriginalMethod.Invoke(null, [41, 42]));
        Assert.AreEqual(42, edit.Method.Invoke(null, [41, 42]));
        Assert.AreEqual(Assert.ContainsSingle(initial.Session.Edits).Fingerprint, edit.Fingerprint);
    }

    /// <summary>
    /// Session-method calls in an original keep their captured implementation after the session method is redefined.
    /// </summary>
    [TestMethod]
    public void Reopen_PreservesPinnedSessionMethodAcrossRedefinition()
    {
        using var initial = new ReplCore();
        string[] definitions = [".method int32 Helper() {", "ldc.i4 21", "ret", "}",
            ".method int32 Read() {", "call Helper", "ret", "}"];
        foreach (var line in definitions) Assert.IsTrue(initial.Handle(line).Succeeded, Transcript(initial));
        var prepared = initial.Handle(".edit Read as Changed");
        Assert.IsNotNull(prepared.EditDocument, Transcript(initial));
        foreach (var line in prepared.EditDocument.Source.Replace("ret", "ldc.i4.1\nadd\nret", StringComparison.Ordinal).Split('\n'))
        {
            Assert.IsTrue(initial.Handle(line).Succeeded, Transcript(initial));
        }

        foreach (var line in new[] { ".method int32 Helper() {", "ldc.i4 84", "ret", "}" })
        {
            Assert.IsTrue(initial.Handle(line).Succeeded, Transcript(initial));
        }

        using var reopened = new ReplCore();
        Assert.IsEmpty(reopened.ReopenSession(SessionCodec.Read(SessionCodec.Write(initial.CaptureSession(new SessionEditor())))));
        var edit = Assert.ContainsSingle(reopened.Session.Edits);
        Assert.AreEqual(21, edit.OriginalMethod.Invoke(null, null));
        Assert.AreEqual(22, edit.Method!.Invoke(null, null));
        Assert.IsTrue(reopened.Handle("call Helper").Succeeded, Transcript(reopened));
        Assert.IsTrue(reopened.Handle("ret").Succeeded, Transcript(reopened));
        Assert.Contains("= 84 : int32", Transcript(reopened));
    }

    /// <summary>
    /// An explicit host reload and a subsequent file reopen keep the original comparison side frozen while adopting rebuilt code.
    /// </summary>
    /// <param name="externalHelper">Whether the changed helper lives in a separate managed dependency.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task HostReload_SaveAndReopenPreserveOriginalComparison(bool externalHelper)
    {
        var token = TestContext.CancellationToken;
        using var fixture = new SessionDependencyFixture();
        var path = Path.Combine(fixture.DirectoryPath, fixture.AssemblyName + ".dll");
        var dependencyName = fixture.AssemblyName + "Dependency";
        var dependencyPath = Path.Combine(fixture.DirectoryPath, dependencyName + ".dll");
        var savedPath = Path.Combine(fixture.DirectoryPath, "comparison.ilrepl.json");
        File.WriteAllBytes(path, CreateImage(fixture.AssemblyName, 21, externalHelper ? dependencyName : null));
        if (externalHelper) File.WriteAllBytes(dependencyPath, CreateImage(dependencyName, 21));
        await using (var controller = await fixture.StartAsync(token))
        {
            if (externalHelper) Assert.IsTrue((await controller.HandleAsync(".load " + dependencyPath, token)).Succeeded);
            var loaded = await controller.HandleAsync(".load " + path, token);
            Assert.IsTrue(loaded.Succeeded, string.Join('\n', loaded.Lines.Select(line => line.PlainText)));
            var reference = ".edit int32 [" + fixture.AssemblyName + "]SnapshotValues::Read() as Changed";
            var prepared = await controller.HandleAsync(reference, token);
            Assert.IsNotNull(prepared.EditDocument, string.Join('\n', prepared.Lines.Select(line => line.PlainText)));
            var edited = prepared.EditDocument.Source.Replace("ret", "ldc.i4.1\nadd\nret", StringComparison.Ordinal);
            foreach (var line in edited.Split('\n'))
            {
                var reply = await controller.HandleAsync(line, token);
                Assert.IsTrue(reply.Succeeded, string.Join('\n', reply.Lines.Select(item => item.PlainText)));
            }

            var rebuilt = externalHelper ? dependencyPath : path;
            AssemblyFileCleanup.Replace(rebuilt, CreateImage(externalHelper ? dependencyName : fixture.AssemblyName, 84));
            var reloaded = await controller.HandleAsync(".load " + rebuilt + " --reload", token);
            Assert.IsTrue(reloaded.Succeeded, string.Join('\n', reloaded.Lines.Select(line => line.PlainText)));
            var saved = await controller.HandleAsync(".session save " + savedPath, token);
            Assert.IsTrue(saved.Succeeded, string.Join('\n', saved.Lines.Select(line => line.PlainText)));
        }

        await using var reopened = await fixture.StartAsync(token);
        var opened = await reopened.HandleAsync(".session open " + savedPath, token);
        Assert.IsTrue(opened.Succeeded, string.Join('\n', opened.Lines.Select(line => line.PlainText)));
        Assert.IsTrue((await reopened.HandleAsync("call int32 [" + fixture.AssemblyName + "]SnapshotValues::Read()", token)).Succeeded);
        var current = await reopened.HandleAsync("ret", token);
        Assert.Contains(line => line.Kind == LineKind.Result && line.PlainText.Contains("= 84 : int32", StringComparison.Ordinal),
            current.Lines);
        var comparison = await reopened.HandleAsync(".compare Changed ()", token);
        Assert.IsNotNull(comparison.PendingComparison, string.Join('\n', comparison.Lines.Select(line => line.PlainText)));
        var report = await reopened.CompareAsync(comparison.PendingComparison.Identity, token);
        Assert.IsNotNull(report.Comparison);
        Assert.AreEqual("21", report.Comparison.Original.Result!.Value);
        Assert.AreEqual("22", report.Comparison.Edited.Result!.Value);
        Assert.AreEqual("different", report.Comparison.Outcome);
    }

    /// <summary>
    /// Reopening a document with rebuilt dependency bytes preserves both the immutable original and its edited helper calls.
    /// </summary>
    /// <param name="externalHelper">Whether the immutable helper lives in another external assembly.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(60_000, CooperativeCancellation = true)]
    public Task Reopen_RebuiltDependencyKeepsOriginalAndPrivateHelperBindings(bool externalHelper) =>
        IsolatedTestProcess.WithDirectoryAsync(TestContext, async directory =>
        {
            var name = "FrozenEdit" + Guid.NewGuid().ToString("N");
            var path = Path.Combine(directory, name + ".dll");
            var helperName = name + "Dependency";
            var helperPath = Path.Combine(directory, helperName + ".dll");
            File.WriteAllBytes(path, CreateImage(name, 21, externalHelper ? helperName : null));
            using var initial = new ReplCore();
            if (externalHelper)
            {
                File.WriteAllBytes(helperPath, CreateImage(helperName, 21));
                Assert.IsTrue(initial.Handle(".load " + helperPath).Succeeded, Transcript(initial));
            }

            Assert.IsTrue(initial.Handle(".load " + path).Succeeded, Transcript(initial));
            var prepared = initial.Handle(".edit int32 [" + name + "]SnapshotValues::Read() as Changed");
            Assert.IsTrue(prepared.Succeeded, Transcript(initial));
            Assert.IsNotNull(prepared.EditDocument);
            var source = prepared.EditDocument.Source.Replace("ret", "ldc.i4.1\nadd\nret", StringComparison.Ordinal);
            foreach (var line in source.Split('\n'))
            {
                Assert.IsTrue(initial.Handle(line).Succeeded, line + "\n" + Transcript(initial));
            }

            var original = Assert.ContainsSingle(initial.Session.Edits);
            Assert.AreEqual(21, original.OriginalMethod.Invoke(null, null));
            Assert.AreEqual(22, original.Method!.Invoke(null, null));
            var document = initial.CaptureSession(new SessionEditor());
            var replacement = CreateImage(externalHelper ? helperName : name, 84);
            var replacedPath = externalHelper ? helperPath : path;
            AssemblyFileCleanup.Replace(replacedPath, replacement);
            var hash = SessionCodec.Hash(replacement);
            var replacedName = AssemblyName.GetAssemblyName(replacedPath).FullName!;
            var changed = document with
            {
                References = [.. document.References.Select(reference => reference.Origin != "assembly" ? reference : reference with
                {
                    Assets = [.. reference.Assets.Select(asset => asset.Name != replacedName ? asset : asset with
                    {
                        Hash = hash, Path = replacedPath, Mvid = null,
                    })],
                })],
                Assets = [.. document.Assets, new SessionAsset { Hash = hash, Image = replacement }],
            };
            var saved = SessionCodec.Read(SessionCodec.Write(changed));
            using var reopened = new ReplCore();

            var diagnostics = reopened.ReopenSession(saved);

            Assert.IsEmpty(diagnostics, string.Join('\n', diagnostics));
            var restored = Assert.ContainsSingle(reopened.Session.Edits);
            Assert.AreEqual(original.Fingerprint, restored.Fingerprint);
            Assert.AreEqual(21, restored.OriginalMethod.Invoke(null, null));
            Assert.AreEqual(22, restored.Method!.Invoke(null, null));
            var comparison = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(reopened.Session, "Changed ()"),
                TestContext.CancellationToken);
            Assert.AreEqual("21", comparison.Original.Result!.Value);
            Assert.AreEqual("22", comparison.Edited.Result!.Value);
            Assert.IsTrue(reopened.Handle("call int32 [" + name + "]SnapshotValues::Read()").Succeeded, Transcript(reopened));
            Assert.IsTrue(reopened.Handle("ret").Succeeded, Transcript(reopened));
            Assert.Contains("= 84 : int32", Transcript(reopened));
        });

    private static byte[] CreateImage(string name, int value, string? dependency = null)
    {
        using var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition(name, new Version(1, 0)), name, ModuleKind.Dll);
        var module = assembly.MainModule;
        var owner = new TypeDefinition("", "SnapshotValues", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed,
            module.TypeSystem.Object);
        module.Types.Add(owner);
        var helper = new MethodDefinition("Helper", MethodAttributes.Private | MethodAttributes.Static, module.TypeSystem.Int32);
        helper.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4, value));
        helper.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        owner.Methods.Add(helper);
        var root = new MethodDefinition("Read", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Int32);
        MethodReference target = helper;
        if (dependency is not null)
        {
            var reference = new AssemblyNameReference(dependency, new Version(1, 0));
            module.AssemblyReferences.Add(reference);
            target = new MethodReference("Read", module.TypeSystem.Int32, new TypeReference("", "SnapshotValues", module, reference));
        }

        root.Body.Instructions.Add(Instruction.Create(OpCodes.Call, target));
        root.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        owner.Methods.Add(root);
        using var image = new MemoryStream();
        assembly.Write(image);
        return image.ToArray();
    }

    private static string Transcript(ReplCore core) => string.Join('\n', core.Transcript.Lines.Select(line => line.PlainText));
}
