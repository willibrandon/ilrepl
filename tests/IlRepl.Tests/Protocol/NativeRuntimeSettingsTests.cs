using System.Reflection;
using System.Reflection.PortableExecutable;
using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Protocol;

namespace IlRepl.Tests.Protocol;

/// <summary>
/// Verifies native compilation settings isolate worker overrides while retaining effective ISA controls.
/// </summary>
[TestClass]
public sealed class NativeRuntimeSettingsTests
{
    /// <summary>
    /// Dynamic targets retain ReadyToRun while capture-owned settings override inherited values only in the child environment.
    /// </summary>
    [TestMethod]
    public void Create_DynamicTargetRetainsReadyToRunAndIsolatesOverrides()
    {
        var package = new NativePackage
        {
            Left = new NativeTarget { Name = "current cell", Cell = new NativeCell { Body = ["ldc.i4.1"] } },
            Options = new NativeOptions
            {
                Environment = new Dictionary<string, string> { ["COMPlus_EnableAVX512F"] = "0", ["APP_VALUE"] = "worker" },
            },
            Environment = new Dictionary<string, string>
            {
                ["DOTNET_ReadyToRun"] = "0", ["COMPlus_TieredCompilation"] = "1", ["DOTNET_JitDisasm"] = "*",
                ["DOTNET_JitDisasmDiffable"] = "1", ["DOTNET_EnableAVX512F"] = "1", ["COMPlus_EnableSSE42"] = "0",
                ["DOTNET_PreferredVectorBitWidth"] = "128", ["APP_VALUE"] = "parent", ["APP_SECRET"] = "not a runtime setting",
                ["DOTNET_STARTUP_HOOKS"] = "hook.dll", ["CORECLR_ENABLE_PROFILING"] = "1", ["CORECLR_PROFILER"] = "profiler-id",
            },
        };

        var environment = NativeRuntimeSettings.Create(package, package.Left, "native-listing.txt", "diagnostic-socket");

        Assert.IsFalse(environment.ContainsKey("DOTNET_ReadyToRun"));
        Assert.IsFalse(environment.ContainsKey("DOTNET_ReadyToRunExcludeList"));
        Assert.AreEqual("0", environment["DOTNET_TieredCompilation"]);
        Assert.AreEqual("0", environment["DOTNET_TieredPGO"]);
        Assert.AreEqual("0", environment["DOTNET_JitDisasmDiffable"]);
        Assert.AreEqual("1", environment["DOTNET_JitDisasmTesting"]);
        Assert.AreEqual("1", environment["DOTNET_JitDisasmSummary"]);
        Assert.AreEqual("1", environment["DOTNET_JitDisasmWithCodeBytes"]);
        Assert.AreEqual("native-listing.txt", environment["DOTNET_JitStdOutFile"]);
        Assert.AreEqual("diagnostic-socket,connect,suspend", environment["DOTNET_DiagnosticPorts"]);
        Assert.Contains("ilrepl.cell.*!IlRepl.Cell:Run", environment["DOTNET_JitDisasm"]);
        Assert.AreEqual("0", environment["DOTNET_EnableAVX512F"]);
        Assert.AreEqual("0", environment["DOTNET_EnableSSE42"]);
        Assert.AreEqual("128", environment["DOTNET_PreferredVectorBitWidth"]);
        Assert.AreEqual("worker", environment["APP_VALUE"]);
        Assert.IsFalse(environment.ContainsKey("DOTNET_STARTUP_HOOKS"));
        Assert.IsFalse(environment.ContainsKey("CORECLR_ENABLE_PROFILING"));
        Assert.IsFalse(environment.ContainsKey("CORECLR_PROFILER"));
        Assert.AreEqual("0", package.Environment["DOTNET_ReadyToRun"]);
        Assert.AreEqual("1", package.Environment["DOTNET_EnableAVX512F"]);
        Assert.AreEqual("parent", package.Environment["APP_VALUE"]);
        Assert.AreEqual("*", package.Environment["DOTNET_JitDisasm"]);
        var described = NativeRuntimeSettings.Describe(environment);
        Assert.AreEqual("0", described["DOTNET_EnableAVX512F"]);
        Assert.IsFalse(described.ContainsKey("APP_SECRET"));
        Assert.IsFalse(described.ContainsKey("DOTNET_JitStdOutFile"));
        Assert.IsFalse(described.ContainsKey("DOTNET_DiagnosticPorts"));
    }

    /// <summary>
    /// Tiered modes and explicit PGO settings become the corresponding worker runtime controls.
    /// </summary>
    /// <param name="tier">The requested tier mode.</param>
    /// <param name="pgo">Whether dynamic profiling is enabled.</param>
    /// <param name="expectedPgo">The runtime setting for dynamic profiling.</param>
    [TestMethod]
    [DataRow("tier0", true, "1")]
    [DataRow("tier0", false, "0")]
    [DataRow("tier1", true, "1")]
    [DataRow("tier1", false, "0")]
    public void Create_TieredModesUseExplicitPgo(string tier, bool pgo, string expectedPgo)
    {
        var package = new NativePackage { Options = new NativeOptions { Tier = tier, Pgo = pgo } };

        var environment = NativeRuntimeSettings.Create(package, package.Left, "listing", "port");

        Assert.AreEqual("1", environment["DOTNET_TieredCompilation"]);
        Assert.AreEqual(expectedPgo, environment["DOTNET_TieredPGO"]);
        Assert.IsFalse(environment.ContainsKey("DOTNET_ReadyToRun"));
    }

    /// <summary>
    /// ReadyToRun exclusion is computed once from both real target images and shared by both worker settings.
    /// </summary>
    [TestMethod]
    public void Create_ReadyToRunComparisonUsesUnionOfTargetAssemblies()
    {
        var left = Target(typeof(object).GetMethod(nameof(ToString), Type.EmptyTypes)!);
        var right = Target(typeof(Console).GetMethod(nameof(Console.WriteLine), Type.EmptyTypes)!);
        var package = new NativePackage { Left = left, Right = right };

        var original = NativeRuntimeSettings.Create(package, left, "left-listing", "left-port");
        var edited = NativeRuntimeSettings.Create(package, right, "right-listing", "right-port");

        Assert.IsFalse(original.ContainsKey("DOTNET_ReadyToRun"));
        Assert.IsFalse(edited.ContainsKey("DOTNET_ReadyToRun"));
        Assert.AreEqual(original["DOTNET_ReadyToRunExcludeList"], edited["DOTNET_ReadyToRunExcludeList"]);
        var excluded = original["DOTNET_ReadyToRunExcludeList"].Split(';');
        Assert.Contains(typeof(object).Assembly.GetName().Name!, excluded);
        Assert.Contains(typeof(Console).Assembly.GetName().Name!, excluded);
        Assert.HasCount(2, excluded);

        static NativeTarget Target(MethodInfo method)
        {
            var image = File.ReadAllBytes(method.Module.Assembly.Location);
            using var pe = new PEReader(new MemoryStream(image, writable: false));
            Assert.IsGreaterThan(0, pe.PEHeaders.CorHeader!.ManagedNativeHeaderDirectory.Size,
                "This test uses the real ReadyToRun framework images shipped with the supported runtime.");
            return new NativeTarget
            {
                Name = method.Name, Method = NativeCapture.Identify(method),
                Assemblies = [new NativeAssembly { Name = method.Module.Assembly.FullName!, Image = image }],
            };
        }
    }

    /// <summary>
    /// Explicit aliases cannot contradict each other or replace worker-owned diagnostic and compilation settings.
    /// </summary>
    /// <param name="key">The reserved environment override.</param>
    [TestMethod]
    [DataRow("DOTNET_TieredCompilation")]
    [DataRow("COMPlus_ReadyToRun")]
    [DataRow("DOTNET_JitDisasm")]
    [DataRow("DOTNET_DiagnosticPorts")]
    [DataRow("DOTNET_DefaultDiagnosticPortSuspend")]
    public void Create_ExplicitReservedOverridesAreRejected(string key)
    {
        var package = new NativePackage { Options = new NativeOptions { Environment = new Dictionary<string, string> { [key] = "0" } } };

        var error = Assert.ThrowsExactly<ReplException>(() => NativeRuntimeSettings.Create(package, package.Left, "listing", "port"));

        Assert.Contains(key, error.Message);
        Assert.Contains("owned by native capture", error.Message);
    }

    /// <summary>
    /// Conflicting legacy and modern aliases fail rather than depending on dictionary iteration order.
    /// </summary>
    [TestMethod]
    public void Create_ContradictoryExplicitAliasesAreRejected()
    {
        var package = new NativePackage
        {
            Options = new NativeOptions
            {
                Environment = new Dictionary<string, string> { ["COMPlus_EnableAVX"] = "0", ["DOTNET_EnableAVX"] = "1" },
            },
        };

        var error = Assert.ThrowsExactly<ReplException>(() => NativeRuntimeSettings.Create(package, package.Left, "listing", "port"));

        Assert.Contains("duplicate runtime setting 'DOTNET_EnableAVX'", error.Message);
    }

    /// <summary>
    /// Inherited diagnostic opt-outs are overridden only in the child so inspection remains available.
    /// </summary>
    /// <param name="key">The runtime diagnostic control disabled by the caller.</param>
    [TestMethod]
    [DataRow("DOTNET_EnableDiagnostics")]
    [DataRow("COMPlus_EnableDiagnostics_IPC")]
    [DataRow("DOTNET_EnableDiagnostics_Tracing")]
    public void Create_InheritedDisabledDiagnosticsAreEnabledWithoutChangingParent(string key)
    {
        var package = new NativePackage
        {
            Environment = new Dictionary<string, string> { [key] = "0", ["DOTNET_DefaultDiagnosticPortSuspend"] = "1" },
        };

        var environment = NativeRuntimeSettings.Create(package, package.Left, "listing", "port");

        Assert.AreEqual("1", environment["DOTNET_EnableDiagnostics"]);
        Assert.AreEqual("1", environment["DOTNET_EnableDiagnostics_IPC"]);
        Assert.AreEqual("1", environment["DOTNET_EnableDiagnostics_Tracing"]);
        Assert.AreEqual("0", environment["DOTNET_DefaultDiagnosticPortSuspend"]);
        Assert.IsFalse(environment.ContainsKey("COMPlus_EnableDiagnostics_IPC"));
        Assert.AreEqual("0", package.Environment[key]);
        Assert.AreEqual("1", package.Environment["DOTNET_DefaultDiagnosticPortSuspend"]);
    }

    /// <summary>
    /// Explicit diagnostic opt-outs fail with the setting name while explicitly enabling diagnostics remains valid.
    /// </summary>
    /// <param name="key">The diagnostic control explicitly set by the caller.</param>
    [TestMethod]
    [DataRow("DOTNET_EnableDiagnostics")]
    [DataRow("COMPlus_EnableDiagnostics_IPC")]
    [DataRow("DOTNET_EnableDiagnostics_Tracing")]
    public void Create_ExplicitDiagnosticsConflictIsActionableAndEnabledValueIsAccepted(string key)
    {
        var package = new NativePackage
        {
            Options = new NativeOptions { Environment = new Dictionary<string, string> { [key] = "0" } },
        };

        var error = Assert.ThrowsExactly<ReplException>(() => NativeRuntimeSettings.Create(package, package.Left, "listing", "port"));

        Assert.Contains(key, error.Message);
        Assert.Contains("must be 1", error.Message);
        Assert.Contains("diagnostics and tracing are required", error.Message);
        var enabled = package with
        {
            Options = package.Options with { Environment = new Dictionary<string, string> { [key] = "1" } },
        };

        var environment = NativeRuntimeSettings.Create(enabled, enabled.Left, "listing", "port");
        Assert.AreEqual("1", environment[key.Replace("COMPlus_", "DOTNET_", StringComparison.Ordinal)]);
    }

    /// <summary>
    /// Startup hooks and profiling overrides explain their separate isolation policy instead of suggesting a nonexistent option.
    /// </summary>
    /// <param name="key">The forbidden worker extension.</param>
    [TestMethod]
    [DataRow("DOTNET_STARTUP_HOOKS")]
    [DataRow("CORECLR_ENABLE_PROFILING")]
    [DataRow("CORECLR_PROFILER")]
    public void Create_ExplicitHooksAndProfilersExplainIsolation(string key)
    {
        var package = new NativePackage { Options = new NativeOptions { Environment = new Dictionary<string, string> { [key] = "1" } } };

        var error = Assert.ThrowsExactly<ReplException>(() => NativeRuntimeSettings.Create(package, package.Left, "listing", "port"));

        Assert.Contains(key, error.Message);
        Assert.Contains("startup hooks and profilers are disabled", error.Message);
        Assert.DoesNotContain("corresponding .jit option", error.Message);
    }

    /// <summary>
    /// Runtime pinning retains the complete preview folder version used by the dotnet host.
    /// </summary>
    /// <param name="version">The installed framework folder name.</param>
    [TestMethod]
    [DataRow("10.0.7")]
    [DataRow("11.0.0-preview.1.26104.118")]
    public void FrameworkVersion_PreservesInstalledPreviewSuffix(string version)
    {
        var path = Path.Join(Path.GetTempPath(), "dotnet", "shared", "Microsoft.NETCore.App", version, "System.Private.CoreLib.dll");

        Assert.AreEqual(version, NativeRuntimeSettings.FrameworkVersion(path));
    }
}
