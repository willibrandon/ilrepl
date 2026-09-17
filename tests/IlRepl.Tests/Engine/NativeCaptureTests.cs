using IlRepl.Engine;
using IlRepl.Protocol;
using IlRepl.Tests.Shared;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Verifies immutable native captures retain actual bodies, dependency identities, and closed generic arguments.
/// </summary>
[TestClass]
public sealed class NativeCaptureTests
{
    /// <summary>
    /// Capturing a caller retains its implementation and exact current trampoline binding across later redefinitions.
    /// </summary>
    [TestMethod]
    public void Create_SessionCallerRetainsBodyAndFrozenTrampolineBinding()
    {
        var session = new Session { DeferActivation = true };
        try
        {
            Add(session, ".method int32 Value() { ldc.i4.s 42; ret }", ".method int32 Caller() { call Value; ret }");
            var caller = session.Methods.Single(method => method.Signature.Name == "Caller");
            var before = NativeCapture.Create(session, "Caller", new NativeOptions());
            var oldBinding = Assert.ContainsSingle(before.Bindings.Where(binding => binding.Name == "Value"),
                string.Join('\n', before.Assemblies.Select(image => image.Role + ": " + image.Name)));
            var oldImage = before.Assemblies.Single(image => image.Name == oldBinding.Implementation.Assembly);
            var oldBytes = oldImage.Image.ToArray();

            Add(session, ".method int32 Value() { ldc.i4.s 43; ret }");
            var after = NativeCapture.Create(session, "Caller", new NativeOptions());
            var newBinding = after.Bindings.Single(binding => binding.Name == "Value");

            Assert.IsNotNull(before.Method);
            Assert.AreEqual(caller.Version.Body.Module.Assembly.FullName, before.Method.Assembly);
            Assert.AreEqual(caller.Version.Body.MetadataToken, before.Method.Token);
            Assert.IsFalse(before.Method.ReturnsPointer);
            Assert.AreNotEqual(caller.Trampoline.Method.Module.Assembly.FullName, before.Method.Assembly);
            Assert.AreEqual(oldBinding.Trampoline.Assembly, newBinding.Trampoline.Assembly);
            Assert.AreNotEqual(oldBinding.Implementation.Assembly, newBinding.Implementation.Assembly);
            Assert.AreEqual(oldImage.Name, oldBinding.Implementation.Assembly);
            Assert.AreSequenceEqual(oldBytes,
                before.Assemblies.Single(image => image.Name == oldBinding.Implementation.Assembly).Image);
            Assert.Contains(image => image.Name == oldBinding.Trampoline.Assembly, before.Assemblies);
            Assert.Contains(image => image.Name == newBinding.Implementation.Assembly, after.Assemblies);
            Assert.AreEqual(before.Fingerprint, after.Fingerprint, "Rebinding a dependency must not rewrite the caller's IL.");
            Assert.IsTrue(session.DeferActivation);
        }
        finally
        {
            session.Reset();
            session.Resolver.Dispose();
        }
    }

    /// <summary>
    /// A dynamic cell is represented by source plus referenced images without activating their module initializers.
    /// </summary>
    [TestMethod]
    public void Create_CellRetainsRecipeAndLoadedOperandDependencyWithoutActivation()
    {
        using var files = new SessionWorkspaceFixture();
        var session = new Session { DeferActivation = true };
        try
        {
            var image = ModuleInitializerFixture.Create(true, files.MarkerPath);
            var reference = session.Resolver.LoadImage(image);
            Add(session, ".args (int32 value = 42)", "ldarg value", "pop", "call int32 Owner::Read()");

            var target = NativeCapture.Create(session, "", new NativeOptions());

            Assert.IsNull(target.Method);
            Assert.IsNotNull(target.Cell);
            Assert.AreSequenceEqual([".args (int32 value = 42)"], target.Cell.Declarations);
            Assert.AreSequenceEqual(["ldarg value", "pop", "call int32 Owner::Read()"], target.Cell.Body);
            var captured = Assert.ContainsSingle(target.Assemblies.Where(item => item.Name == reference.FullName));
            Assert.AreSequenceEqual(image, captured.Image);
            Assert.AreEqual("reference", captured.Role);
            Assert.IsFalse(File.Exists(files.MarkerPath));
            Assert.IsTrue(session.DeferActivation);
            Assert.IsNull(Assert.ContainsSingle(session.Cell.Arguments).Value);
            Assert.AreEqual(64, target.Fingerprint.Length);
        }
        finally
        {
            session.Reset();
            session.Resolver.Dispose();
        }
    }

    /// <summary>
    /// Edited and original selectors preserve distinct images and fingerprints for the committed implementations.
    /// </summary>
    [TestMethod]
    public void Create_EditAndOriginalSelectTheirOwnImages()
    {
        var session = IlLines.Load(".method int32 Value() { ldc.i4.s 42; ret }");
        try
        {
            var edit = session.PrepareEdit("Value", "Copy");
            session.CommitEdit("Copy", edit.Source.Replace("42", "43", StringComparison.Ordinal));

            var original = NativeCapture.Create(session, "Copy", new NativeOptions { Original = true });
            var edited = NativeCapture.Create(session, "Copy", new NativeOptions());

            Assert.IsNotNull(original.Method);
            Assert.IsNotNull(edited.Method);
            Assert.AreEqual(edit.Original.Requested.Module.Assembly.FullName, original.Method.Assembly);
            Assert.AreEqual(edit.Method!.Module.Assembly.FullName, edited.Method.Assembly);
            Assert.AreEqual(edit.Original.Requested.MetadataToken, original.Method.Token);
            Assert.AreEqual(edit.Method.MetadataToken, edited.Method.Token);
            Assert.AreNotEqual(original.Method.Assembly, edited.Method.Assembly);
            Assert.AreNotEqual(original.Fingerprint, edited.Fingerprint);
            Assert.IsNull(original.Cell);
            Assert.IsNull(edited.Cell);
        }
        finally
        {
            session.Reset();
            session.Resolver.Dispose();
        }
    }

    /// <summary>
    /// Closed owner and method generic arguments survive capture instead of becoming generic definitions.
    /// </summary>
    /// <param name="owner">Whether the generic argument belongs to the declaring type.</param>
    /// <param name="strings">Whether the specialization uses a reference type.</param>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void Create_ClosedGenericsPreserveExactInstantiation(bool owner, bool strings)
    {
        var session = owner
            ? IlLines.Load(".class public Choice`1<T> {", ".method public static !0 Pick(!0 value) { ldarg.0; ret }", "}")
            : IlLines.Load(".class public Choice {", ".method public static !!0 Pick<T>(!!0 value) { ldarg.0; ret }", "}");
        try
        {
            var argument = strings ? "string" : "int32";
            var selector = owner ? $"!0 Choice`1<{argument}>::Pick(!0)" : $"!!0 Choice::Pick<{argument}>(!!0)";

            var target = NativeCapture.Create(session, selector, new NativeOptions());

            Assert.IsNotNull(target.Method);
            var arguments = owner ? target.Method.TypeArguments : target.Method.MethodArguments;
            Assert.AreEqual((strings ? typeof(string) : typeof(int)).AssemblyQualifiedName, Assert.ContainsSingle(arguments));
            Assert.IsEmpty(owner ? target.Method.MethodArguments : target.Method.TypeArguments);
            Assert.AreEqual(owner ? "Choice`1" : "Choice", target.Method.Type);
            Assert.AreEqual(strings, target.Method.ReturnsPointer);
            Assert.Contains(image => image.Name == target.Method.Assembly, target.Assemblies);
        }
        finally
        {
            session.Reset();
            session.Resolver.Dispose();
        }
    }

    /// <summary>
    /// Execution without required arguments fails before activation instead of fabricating inputs.
    /// </summary>
    [TestMethod]
    public void Create_ParameterizedRunRequiresExplicitArguments()
    {
        var session = new Session { DeferActivation = true };
        try
        {
            Add(session, ".method int32 Value(int32 input) { ldarg.0; ret }");

            var error = Assert.ThrowsExactly<ReplException>(() =>
                NativeCapture.Create(session, "Value", new NativeOptions { Run = true }));

            Assert.Contains("requires 1 literal arguments or using Scenario", error.Message);
            Assert.IsTrue(session.DeferActivation);
            var inspected = NativeCapture.Create(session, "Value", new NativeOptions());
            Assert.IsNotNull(inspected.Method);
        }
        finally
        {
            session.Reset();
            session.Resolver.Dispose();
        }
    }

    private static void Add(Session session, params string[] source)
    {
        foreach (var line in IlLines.Expand(source)) session.AddLine(line);
    }
}
