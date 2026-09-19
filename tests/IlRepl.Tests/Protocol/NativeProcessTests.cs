using System.Runtime.InteropServices;
using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Repl;
using IlRepl.Tests.Engine;

namespace IlRepl.Tests.Protocol;

/// <summary>
/// Verifies real CoreCLR worker disassembly, startup diagnostics, and event-driven tier promotion.
/// </summary>
[TestClass]
public sealed class NativeProcessTests
{
    /// <summary>
    /// Supplies cancellation to actual native inspection processes.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// FullOpts compilation produces attributed native code without executing the selected file-writing implementation.
    /// </summary>
    /// <param name="collectible">Whether the worker reproduces the session's collectible loading behavior.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(90_000, CooperativeCancellation = true)]
    public async Task Inspect_FullOptsCapturesActualBodyWithoutExecution(bool collectible)
    {
        using var files = new SessionWorkspaceFixture();
        using var core = new ReplCore();
        Submit(core, ".method int32 NativeArithmetic(int32 value) {", "ldstr " + LiteralParser.Escape(files.MarkerPath),
            "ldstr \"body executed\"", "call void File::WriteAllText(string, string)", "ldarg.0", "ldc.i4.2", "mul", "ret", "}");
        var selected = core.Session.Methods.Single().Version.Body;
        var request = core.Handle(".jit NativeArithmetic" + (collectible ? " --collectible" : ""));
        Assert.IsTrue(request.Succeeded, Plain(core));
        Assert.IsNotNull(request.NativePackage);

        var report = await ProcessNativeRunner.RunAsync(request.NativePackage, TestContext.CancellationToken);

        Assert.AreEqual("complete", report.Outcome, report.Left.Detail + "\n" + report.Left.StandardError);
        Assert.AreEqual("complete", report.Left.Outcome, report.Left.Detail + "\n" + report.Left.StandardError);
        Assert.IsNull(report.Right);
        Assert.AreEqual("NativeArithmetic", report.Left.Name);
        Assert.AreEqual(request.NativePackage.Left.Fingerprint, report.Left.Fingerprint);
        Assert.IsNotNull(report.Left.Implementation);
        Assert.AreEqual(selected.Module.Assembly.FullName, report.Left.Implementation.Assembly);
        Assert.AreEqual(selected.DeclaringType!.FullName, report.Left.Implementation.Type);
        Assert.AreEqual(selected.MetadataToken, report.Left.Implementation.Token);
        Assert.IsFalse(report.Left.Implementation.ReturnsPointer);
        Assert.IsEmpty(report.Left.Implementation.TypeArguments);
        Assert.IsEmpty(report.Left.Implementation.MethodArguments);
        Assert.AreEqual(selected.Module.ModuleVersionId, report.Left.ModuleVersionId);
        Assert.AreNotEqual(Guid.Empty, report.Left.ModuleVersionId);
        Assert.AreEqual(RuntimeInformation.RuntimeIdentifier, report.Left.RuntimeIdentifier);
        Assert.AreEqual("target body and user module initializers not authorized", report.Left.Authorization);
        Assert.AreEqual(0, report.Left.Invocations);
        Assert.AreEqual(collectible, report.Left.Collectible);
        Assert.AreEqual(RuntimeInformation.ProcessArchitecture.ToString(), report.Left.Architecture);
        Assert.Contains(Environment.Version.ToString(), report.Left.Runtime);
        Assert.Contains("SHA256", report.Left.Jit);
        Assert.IsFalse(report.Left.Settings.ContainsKey("DOTNET_ReadyToRun"));
        var compilation = Assert.ContainsSingle(report.Left.Compilations);
        Assert.Contains("NativeArithmetic", compilation.Method);
        Assert.Contains(compilation.Method, report.Left.Implementation.JitNames);
        Assert.AreEqual("FullOpts", compilation.Tier);
        Assert.IsGreaterThan(0, compilation.CodeSize);
        Assert.AreNotEqual(0UL, compilation.MethodId);
        Assert.AreNotEqual(0UL, compilation.Address);
        Assert.IsNotEmpty(compilation.Normalized);
        Assert.Contains("Assembly listing for method", compilation.Listing);
        Assert.Contains(role => role.Contains("implementation:", StringComparison.Ordinal)
            && role.Contains(selected.Name, StringComparison.Ordinal), report.Left.Roles);
        Assert.IsFalse(File.Exists(files.MarkerPath), "Preparing the selected body must not invoke its file-writing instructions.");
        Assert.IsEmpty(report.Left.StandardOutput);
        Assert.IsEmpty(report.Left.StandardError);
        Assert.IsEmpty(report.Left.UnattributedListings);
        Assert.IsTrue(core.Status.CellIsEmpty);
        Assert.IsFalse(report.Left.IsCapability);
        core.ReportNative(report);
        var output = Plain(core);
        Assert.Contains("NativeArithmetic: complete; .NET", output);
        Assert.Contains("0 workload invocations", output);
        Assert.Contains("FullOpts;", output);
        Assert.Contains("native bytes", output);
        Assert.DoesNotContain("IL fingerprint:", output);
        Assert.DoesNotContain("module " + selected.Module.ModuleVersionId, output);
        Assert.DoesNotContain("JIT: ", output);
        Assert.DoesNotContain("DOTNET_JitDisasm=", output);
        Assert.DoesNotContain(report.Left.Authorization, output);
        foreach (var header in NativeDisassembly.Headers(compilation))
        {
            Assert.Contains("  " + header, core.Transcript.Lines.Select(line => line.PlainText));
        }
    }

    /// <summary>
    /// Capability inspection compiles ilrepl's own probe and reports the real host runtime and effective ISA settings.
    /// </summary>
    [TestMethod]
    [Timeout(90_000, CooperativeCancellation = true)]
    public async Task Inspect_InfoProvesHostDisassemblyCapability()
    {
        using var core = new ReplCore();
        var request = core.Handle(".jit --info");
        Assert.IsTrue(request.Succeeded, Plain(core));
        Assert.IsNotNull(request.NativePackage);

        var report = await ProcessNativeRunner.RunAsync(request.NativePackage, TestContext.CancellationToken);

        Assert.AreEqual("complete", report.Outcome, report.Left.Detail + "\n" + report.Left.StandardError);
        Assert.AreEqual("host capabilities", report.Left.Name);
        Assert.IsTrue(report.Left.IsCapability);
        Assert.AreEqual(RuntimeInformation.ProcessArchitecture.ToString(), report.Left.Architecture);
        Assert.Contains(Environment.Version.ToString(), report.Left.Runtime);
        Assert.Contains("SHA256", report.Left.Jit);
        Assert.IsNotEmpty(report.Left.InstructionSets);
        Assert.AreEqual(0, report.Left.Invocations);
        var compiled = Assert.ContainsSingle(report.Left.Compilations);
        Assert.Contains("NativeCapability:Probe", compiled.Method);
        Assert.AreEqual("FullOpts", compiled.Tier);
        Assert.IsGreaterThan(0, compiled.CodeSize);
        Assert.IsTrue(core.Status.CellIsEmpty);
        Assert.IsEmpty(core.Session.Methods);
        core.ReportNative(report);
        var output = Plain(core);
        Assert.Contains("JIT: " + report.Left.Jit, output);
        Assert.Contains("effective ISA:", output);
        var settings = core.Transcript.Lines.Select(line => line.PlainText.Trim())
            .Where(line => report.Left.Settings.Keys.Any(key => line.StartsWith(key + "=", StringComparison.Ordinal))).ToArray();
        Assert.AreSequenceEqual(report.Left.Settings.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => pair.Key + "=" + pair.Value), settings);
        Assert.DoesNotContain("static access", output);
        Assert.DoesNotContain("static-access", output);
        Assert.DoesNotContain("not authorized", output);
    }

    /// <summary>
    /// A release CoreCLR compilation reports the actual small helper it inlines even when CIL identifiers contain spaces.
    /// </summary>
    /// <param name="quoted">Whether the declaring type, caller, and helper have quoted whitespace identifiers.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(90_000, CooperativeCancellation = true)]
    public async Task Inspect_RecordsRealInlineeEventsIncludingWhitespaceNames(bool quoted)
    {
        using var core = new ReplCore();
        var owner = quoted ? "'Inline Owner'" : "InlineOwner";
        var helper = quoted ? "'Small Helper'" : "SmallHelper";
        var caller = quoted ? "'Calling Method'" : "Caller";
        Submit(core, ".class public " + owner + " {",
            ".method public static int32 " + helper + "(int32 value) cil managed aggressiveinlining {",
            "ldarg.0", "ldc.i4.1", "add", "ret", "}",
            ".method public static int32 " + caller + "(int32 value) {",
            "ldarg.0", "call int32 " + owner + "::" + helper + "(int32)", "ret", "}", "}");
        var prepared = core.Handle(".jit int32 " + owner + "::" + caller + "(int32)");
        Assert.IsTrue(prepared.Succeeded, Plain(core));
        Assert.IsNotNull(prepared.NativePackage);

        var report = await ProcessNativeRunner.RunAsync(prepared.NativePackage, TestContext.CancellationToken);

        Assert.AreEqual("complete", report.Outcome, report.Left.Detail + "\n" + report.Left.StandardError);
        Assert.AreEqual(0, report.Left.Invocations);
        var compilation = Assert.ContainsSingle(report.Left.Compilations);
        Assert.Contains(caller.Trim('\''), compilation.Method);
        Assert.Contains(inlinee => inlinee.Contains(owner.Trim('\''), StringComparison.Ordinal)
            && inlinee.Contains(helper.Trim('\''), StringComparison.Ordinal), compilation.Inlinees,
            "The release event stream must report the helper as an inline contributor to the selected caller.");
        Assert.DoesNotContain(line => line.Contains(helper.Trim('\''), StringComparison.Ordinal), compilation.Normalized,
            "The inlined helper must not remain an emitted call target.");
    }

    /// <summary>
    /// A paced real workload reaches published Tier1 code and reports the invocations actually required.
    /// </summary>
    [TestMethod]
    [Timeout(90_000, CooperativeCancellation = true)]
    public async Task Inspect_Tier1ContinuesUntilPublishedTierIsObserved()
    {
        using var core = new ReplCore();
        Submit(core, ".method int32 NativeArithmetic(int32 value) { ldarg.0; ldc.i4.2; mul; ldc.i4.1; add; ret }");
        var request = core.Handle(".jit NativeArithmetic (21) --tier tier1");
        Assert.IsTrue(request.Succeeded, Plain(core));
        Assert.IsNotNull(request.NativePackage);

        var report = await ProcessNativeRunner.RunAsync(request.NativePackage, TestContext.CancellationToken);

        Assert.AreEqual("complete", report.Outcome, report.Left.Detail + "\n" + report.Left.StandardError);
        Assert.IsFalse(report.Left.Collectible);
        Assert.IsInRange(2, request.NativePackage.Options.Iterations, report.Left.Invocations);
        Assert.AreEqual("1", report.Left.Settings["DOTNET_TieredCompilation"]);
        Assert.AreEqual("1", report.Left.Settings["DOTNET_TieredPGO"]);
        Assert.Contains(compilation => compilation.Tier.Contains("Tier0", StringComparison.Ordinal), report.Left.Compilations);
        Assert.Contains(compilation => compilation.Tier == "Tier1", report.Left.Compilations);
        var optimized = report.Left.Compilations.Last(compilation => compilation.Tier == "Tier1");
        Assert.IsGreaterThan(0, optimized.CodeSize);
        Assert.AreNotEqual(0UL, optimized.MethodId);
        Assert.AreNotEqual(0UL, optimized.Address);
        Assert.IsNotEmpty(optimized.Normalized);
        Assert.IsTrue(core.Status.CellIsEmpty);
    }

    /// <summary>
    /// A framework image is recompiled with a targeted ReadyToRun exclusion and worker-scoped ISA settings.
    /// </summary>
    [TestMethod]
    [Timeout(90_000, CooperativeCancellation = true)]
    public async Task Inspect_ReadyToRunFrameworkMethodUsesTargetedExclusionAndIsaOverride()
    {
        using var core = new ReplCore();
        var request = core.Handle(".jit int32 Math::Abs(int32) --env DOTNET_EnableAVX2=0");
        Assert.IsTrue(request.Succeeded, Plain(core));
        Assert.IsNotNull(request.NativePackage);

        var report = await ProcessNativeRunner.RunAsync(request.NativePackage, TestContext.CancellationToken);

        Assert.AreEqual("complete", report.Outcome, report.Left.Detail + "\n" + report.Left.StandardError);
        Assert.Contains("System.Private.CoreLib", report.Left.Settings["DOTNET_ReadyToRunExcludeList"]);
        Assert.IsFalse(report.Left.Settings.ContainsKey("DOTNET_ReadyToRun"));
        Assert.AreEqual("0", report.Left.Settings["DOTNET_EnableAVX2"]);
        Assert.DoesNotContain("System.Runtime.Intrinsics.X86.Avx2", report.Left.InstructionSets);
        Assert.AreEqual(0, report.Left.Invocations);
        var compiled = Assert.ContainsSingle(report.Left.Compilations);
        Assert.Contains("Math:Abs(int)", compiled.Method);
        Assert.AreEqual("FullOpts", compiled.Tier);
        Assert.IsGreaterThan(0, compiled.CodeSize);
    }

    /// <summary>
    /// Closed generic owner and method specializations produce code for their requested value or reference instantiation.
    /// </summary>
    /// <param name="owner">Whether the generic argument belongs to the declaring type.</param>
    /// <param name="strings">Whether the specialization uses a shared reference-type body.</param>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    [Timeout(90_000, CooperativeCancellation = true)]
    public async Task Inspect_ClosedGenericSpecializationsProduceAttributedCode(bool owner, bool strings)
    {
        using var core = new ReplCore();
        Submit(core, owner ? ".class public Choice`1<T> {" : ".class public Choice {",
            owner ? ".method public static !0 Pick(!0 value) { ldarg.0; ret }"
                : ".method public static !!0 Pick<T>(!!0 value) { ldarg.0; ret }", "}");
        var argument = strings ? "string" : "int32";
        var selector = owner ? $"!0 Choice`1<{argument}>::Pick(!0)" : $"!!0 Choice::Pick<{argument}>(!!0)";
        var request = core.Handle(".jit " + selector);
        Assert.IsTrue(request.Succeeded, Plain(core));
        Assert.IsNotNull(request.NativePackage);

        var report = await ProcessNativeRunner.RunAsync(request.NativePackage, TestContext.CancellationToken);

        Assert.AreEqual("complete", report.Outcome, report.Left.Detail + "\n" + report.Left.StandardError);
        Assert.AreEqual(0, report.Left.Invocations);
        var compiled = Assert.ContainsSingle(report.Left.Compilations);
        Assert.Contains("Choice", compiled.Method);
        Assert.Contains("Pick", compiled.Method);
        Assert.AreEqual("FullOpts", compiled.Tier);
        Assert.IsGreaterThan(0, compiled.CodeSize);
        Assert.AreNotEqual(0UL, compiled.MethodId);
        Assert.AreNotEqual(0UL, compiled.Address);
        Assert.Contains(role => role.Contains(strings ? "string" : "int32", StringComparison.Ordinal), report.Left.Roles);
    }

    /// <summary>
    /// Re-emitting a cell resolves its external method against the captured image and leaves execution explicit.
    /// </summary>
    [TestMethod]
    [Timeout(90_000, CooperativeCancellation = true)]
    public async Task Inspect_CellResolvesCapturedExternalMethodImage()
    {
        using var core = new ReplCore();
        var bytes = await File.ReadAllBytesAsync(SampleHost.Samples.GreeterDll, TestContext.CancellationToken);
        var assembly = core.Session.Resolver.LoadImage(bytes);
        Submit(core, "ldc.i4.s 20", "ldc.i4.s 22", "call int32 Greeter.Hello::Add(int32, int32)");
        var request = core.Handle(".jit");
        Assert.IsTrue(request.Succeeded, Plain(core));
        Assert.IsNotNull(request.NativePackage);
        Assert.IsNotNull(request.NativePackage.Left.Cell);
        Assert.IsNull(request.NativePackage.Left.Method);
        var captured = Assert.ContainsSingle(request.NativePackage.Left.Assemblies.Where(image => image.Name == assembly.FullName));
        Assert.AreSequenceEqual(bytes, captured.Image);

        var report = await ProcessNativeRunner.RunAsync(request.NativePackage, TestContext.CancellationToken);

        Assert.AreEqual("complete", report.Outcome, report.Left.Detail + "\n" + report.Left.StandardError);
        Assert.AreEqual(0, report.Left.Invocations);
        Assert.Contains("Run", Assert.ContainsSingle(report.Left.Compilations).Method);
        Assert.IsFalse(core.Status.CellIsEmpty);
        Assert.AreEqual(42, core.Session.Run().Value);
    }

    /// <summary>
    /// Methods that opt out of ordinary tiering retain their metadata policy instead of fabricating the requested tier.
    /// </summary>
    /// <param name="attribute">The method implementation policy supplied in the original IL.</param>
    /// <param name="tier">The incompatible tier requested by the user.</param>
    /// <param name="observed">The actual runtime compilation policy.</param>
    [TestMethod]
    [DataRow("aggressiveoptimization", "tier0", "FullOpts")]
    [DataRow("aggressiveoptimization", "tier1", "FullOpts")]
    [DataRow("nooptimization", "tier0", "MinOpts")]
    [DataRow("nooptimization", "tier1", "MinOpts")]
    [Timeout(90_000, CooperativeCancellation = true)]
    public async Task Inspect_MethodPolicyNeverClaimsIncompatibleRequestedTier(string attribute, string tier, string observed)
    {
        using var core = new ReplCore();
        Submit(core, ".class public Policy {",
            ".method public static int32 Value(int32 input) cil managed " + attribute + " { ldarg.0; ldc.i4.1; add; ret }", "}");
        var command = ".jit int32 Policy::Value(int32)" + (tier == "tier1" ? " (41) --iterations 1" : "")
            + " --tier " + tier + " --timeout 2s";
        var request = core.Handle(command);
        Assert.IsTrue(request.Succeeded, Plain(core));
        Assert.IsNotNull(request.NativePackage);

        var report = await ProcessNativeRunner.RunAsync(request.NativePackage, TestContext.CancellationToken);

        var compilation = Assert.ContainsSingle(report.Left.Compilations);
        Assert.AreEqual(observed, compilation.Tier);
        Assert.AreNotEqual("complete", report.Outcome, report.Left.Detail + "; observed " + compilation.Tier);
        Assert.AreEqual(tier == "tier1" ? 1 : 0, report.Left.Invocations);
        Assert.IsNotNull(report.Left.Detail);
        Assert.Contains("tier", report.Left.Detail);
        Assert.IsTrue(core.Status.CellIsEmpty);
    }

    /// <summary>
    /// A captured external image remains executable after its original file is replaced with invalid bytes.
    /// </summary>
    [TestMethod]
    [Timeout(90_000, CooperativeCancellation = true)]
    public Task Inspect_CapturedImageSurvivesChangedSourceFile() => IsolatedTestProcess.WithDirectoryAsync(TestContext, async directory =>
    {
        var path = Path.Combine(directory, "Greeter.dll");
        File.Copy(SampleHost.Samples.GreeterDll, path);
        using var core = new ReplCore();
        core.Session.Resolver.Load(path);
        Submit(core, "ldc.i4.s 20", "ldc.i4.s 22", "call int32 Greeter.Hello::Add(int32, int32)", "call void Console::Write(int32)");
        var request = core.Handle(".jit --run");
        Assert.IsTrue(request.Succeeded, Plain(core));
        Assert.IsNotNull(request.NativePackage);
        var fingerprint = request.NativePackage.Left.Fingerprint;
        var replacement = path + ".replacement";
        await File.WriteAllTextAsync(replacement, "replaced after capture", TestContext.CancellationToken);
        File.Replace(replacement, path, null);

        var report = await ProcessNativeRunner.RunAsync(request.NativePackage, TestContext.CancellationToken);

        Assert.AreEqual("complete", report.Outcome, report.Left.Detail + "\n" + report.Left.StandardError);
        Assert.AreEqual(fingerprint, report.Left.Fingerprint);
        Assert.AreEqual(1, report.Left.Invocations);
        Assert.AreEqual("42", report.Left.StandardOutput);
        Assert.IsNotEmpty(Assert.ContainsSingle(report.Left.Compilations).Listing);
        Assert.AreEqual("replaced after capture", await File.ReadAllTextAsync(path, TestContext.CancellationToken));
    });

    private static string Plain(ReplCore core) => string.Join('\n', core.Transcript.Lines.Select(line => line.PlainText));

    private static void Submit(ReplCore core, params string[] source)
    {
        foreach (var line in IlLines.Expand(source))
        {
            Assert.IsTrue(core.Handle(line).Succeeded, line + "\n" + Plain(core));
        }
    }
}
