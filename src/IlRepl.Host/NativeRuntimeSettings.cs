using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using IlRepl.Engine;
using IlRepl.Protocol;

namespace IlRepl.Host;

/// <summary>
/// Builds worker-only CoreCLR settings while retaining explicitly inherited ISA controls.
/// </summary>
public static class NativeRuntimeSettings
{
    private static readonly HashSet<string> Owned = new(StringComparer.OrdinalIgnoreCase)
    {
        "JitDisasm", "JitDisasmSummary", "JitDisasmDiffable", "JitDisasmWithCodeBytes", "JitDisasmTesting", "JitStdOutFile",
        "TieredCompilation", "TieredPGO", "ReadyToRun", "ReadyToRunExcludeList", "DiagnosticPorts", "EnableEventPipe",
        "EventPipeConfig", "EventPipeOutputPath", "JitDisasmAssemblies", "JitDump", "JitDumpIR", "JitLateDisasm",
        "DefaultDiagnosticPortSuspend",
    };

    private static readonly HashSet<string> Diagnostics = new(StringComparer.OrdinalIgnoreCase)
    {
        "DOTNET_EnableDiagnostics", "DOTNET_EnableDiagnostics_IPC", "DOTNET_EnableDiagnostics_Tracing",
    };

    /// <summary>
    /// Gets the exact installed framework version, including any preview suffix, from the loaded CoreLib path.
    /// </summary>
    /// <param name="coreLibPath">The loaded System.Private.CoreLib image.</param>
    /// <returns>The framework directory name accepted by dotnet --fx-version.</returns>
    public static string FrameworkVersion(string coreLibPath) => Path.GetFileName(Path.GetDirectoryName(coreLibPath))
        ?? throw new ReplException("cannot identify the loaded CoreCLR framework directory");

    /// <summary>
    /// Produces the complete child environment and rejects conflicting capture overrides.
    /// </summary>
    /// <param name="package">Both sides and their explicit settings.</param>
    /// <param name="target">The side whose body must be captured.</param>
    /// <param name="listing">The dedicated JIT output file.</param>
    /// <param name="port">The reverse diagnostics endpoint.</param>
    /// <returns>The child environment, without changes to the parent.</returns>
    public static Dictionary<string, string> Create(NativePackage package, NativeTarget target, string listing, string port)
    {
        var result = new Dictionary<string,
            string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var (key, value) in package.Environment.OrderBy(pair => pair.Key.StartsWith("DOTNET_", StringComparison.Ordinal) ? 1 : 0))
        {
            var canonical = Canonical(key);
            if (Unsafe(key) || Diagnostics.Contains(canonical)
                || canonical.StartsWith("DOTNET_", StringComparison.Ordinal) && Owned.Contains(canonical[7..]))
            {
                continue;
            }

            result[canonical] = value;
        }

        var explicitKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in package.Options.Environment)
        {
            var canonical = Canonical(key);
            if (Unsafe(key))
            {
                throw new ReplException($"'{key}' cannot be set for native inspection; startup hooks and profilers are disabled");
            }

            if (Diagnostics.Contains(canonical) && value != "1")
            {
                throw new ReplException($"'{key}' must be 1 for native inspection; CoreCLR diagnostics and tracing are required");
            }

            if (canonical.StartsWith("DOTNET_", StringComparison.Ordinal) && Owned.Contains(canonical[7..]))
            {
                throw new ReplException($"'{key}' is owned by native capture and cannot be overridden with --env");
            }

            if (!explicitKeys.Add(canonical))
            {
                throw new ReplException($"duplicate runtime setting '{canonical}'");
            }

            result[canonical] = value;
        }

        foreach (var key in Diagnostics)
        {
            result[key] = "1";
        }

        result["DOTNET_TieredCompilation"] = package.Options.Tier == "fullopts" ? "0" : "1";
        result["DOTNET_TieredPGO"] = package.Options.Pgo && package.Options.Tier != "fullopts" ? "1" : "0";
        result["DOTNET_JitDisasm"] = Filter(target, package.Options.Info) + " ilrepl.native.probes.*!*";
        result["DOTNET_JitDisasmDiffable"] = "0";
        result["DOTNET_JitDisasmWithCodeBytes"] = "1";
        result["DOTNET_JitDisasmTesting"] = "1";
        result["DOTNET_JitDisasmSummary"] = "1";
        result["DOTNET_JitStdOutFile"] = listing;
        result["DOTNET_DiagnosticPorts"] = port + ",connect,suspend";
        result["DOTNET_DefaultDiagnosticPortSuspend"] = "0";
        var exclude = new List<string>();
        foreach (var side in new[] { package.Left, package.Right }.OfType<NativeTarget>())
        {
            if (side.Method is not { } method)
            {
                continue;
            }

            var image = side.Assemblies.FirstOrDefault(assembly => assembly.Name == method.Assembly)?.Image;
            if (image is null)
            {
                var assembly = Assembly.Load(new AssemblyName(method.Assembly));
                image = File.ReadAllBytes(assembly.Location);
            }

            using var pe = new PEReader(new MemoryStream(image, writable: false));
            if (pe.PEHeaders.CorHeader?.ManagedNativeHeaderDirectory.Size > 0)
            {
                exclude.Add(new AssemblyName(method.Assembly).Name!);
            }
        }

        if (exclude.Any(name => name.Any(character => char.IsWhiteSpace(character) || character is ';' or ',')))
        {
            result["DOTNET_ReadyToRun"] = "0";
        }
        else if (exclude.Count != 0)
        {
            result["DOTNET_ReadyToRunExcludeList"] = string.Join(';', exclude.Distinct(StringComparer.Ordinal));
        }

        return result;
    }

    /// <summary>
    /// Returns only relevant code-generation settings for a reproducible report.
    /// </summary>
    /// <param name="environment">The effective worker environment.</param>
    /// <returns>The non-secret runtime settings.</returns>
    public static Dictionary<string, string> Describe(IReadOnlyDictionary<string, string> environment) => environment
        .Where(pair => Relevant(pair.Key) && !pair.Key.Contains("Path", StringComparison.Ordinal)
            && !pair.Key.Contains("File", StringComparison.Ordinal) && pair.Key != "DOTNET_DiagnosticPorts")
        .OrderBy(pair => pair.Key, StringComparer.Ordinal)
        .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    private static bool Relevant(string key) => key.StartsWith("DOTNET_Enable", StringComparison.Ordinal)
        || key.StartsWith("DOTNET_Jit", StringComparison.Ordinal) || key.StartsWith("DOTNET_Tiered", StringComparison.Ordinal)
        || key.StartsWith("DOTNET_TC_", StringComparison.Ordinal) || key.StartsWith("DOTNET_ReadyToRun", StringComparison.Ordinal)
        || key == "DOTNET_PreferredVectorBitWidth";

    private static string Filter(NativeTarget target, bool info)
    {
        if (info)
        {
            return "ilrepl.native.capability!IlRepl.NativeCapability:Probe(int)";
        }

        if (target.Method is not { } method)
        {
            return "ilrepl.cell.*!IlRepl.Cell:Run(*) ilrepl.cell.*!IlRepl.CellBody:Run(*)";
        }

        var captured = target.Assemblies.FirstOrDefault(assembly => assembly.Name == method.Assembly);
        var image = captured?.Image ?? File.ReadAllBytes(Assembly.Load(new AssemblyName(method.Assembly)).Location);
        using var pe = new PEReader(new MemoryStream(image, writable: false));
        var reader = pe.GetMetadataReader();
        var definition = reader.GetMethodDefinition((MethodDefinitionHandle)MetadataTokens.Handle(method.Token));
        var name = reader.GetString(definition.Name);
        return Escape(new AssemblyName(method.Assembly).Name!) + "!" + Escape(method.Type)
            + ":" + Escape(name) + "(*)";
    }

    private static string Escape(string name) => new([.. name.Select(character =>
        char.IsWhiteSpace(character) || character is ':' or '!' or '(' or ')' or '*' or '?' ? '?' : character)]);

    private static string Canonical(string key) => key.StartsWith("COMPlus_", StringComparison.OrdinalIgnoreCase)
        ? "DOTNET_" + key[8..] : key;

    private static bool Unsafe(string key) => key.Contains("PROFILER", StringComparison.OrdinalIgnoreCase)
        || key.EndsWith("ENABLE_PROFILING", StringComparison.OrdinalIgnoreCase)
        || key.EndsWith("STARTUP_HOOKS", StringComparison.OrdinalIgnoreCase);
}
