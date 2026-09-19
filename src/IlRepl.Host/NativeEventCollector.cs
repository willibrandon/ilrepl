using IlRepl.Protocol;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

namespace IlRepl.Host;

/// <summary>
/// Records published code and observed inline relationships before managed worker startup.
/// </summary>
internal sealed class NativeEventCollector
{
    private readonly Lock _gate = new();
    private readonly List<NativeCodeEvent> _methods = [];
    private readonly Dictionary<ulong, string> _assemblies = [];
    private readonly Dictionary<ulong, ulong> _modules = [];
    private readonly Dictionary<int, (long Method, List<string> Inlinees)> _compiling = [];

    /// <summary>
    /// Attaches callbacks before the source starts processing its stream.
    /// </summary>
    /// <param name="source">The live EventPipe source.</param>
    internal NativeEventCollector(EventPipeEventSource source)
    {
        source.Clr.LoaderAssemblyLoad += data =>
        {
            lock (_gate)
            {
                _assemblies[(ulong)data.AssemblyID] = data.FullyQualifiedAssemblyName;
            }
        };

        source.Clr.LoaderModuleLoad += data =>
        {
            lock (_gate)
            {
                _modules[(ulong)data.ModuleID] = (ulong)data.AssemblyID;
            }
        };

        source.Clr.MethodJittingStarted += data =>
        {
            lock (_gate)
            {
                _compiling[data.ThreadID] = (data.MethodID, []);
            }
        };

        source.Clr.MethodInliningSucceeded += data =>
        {
            lock (_gate)
            {
                if (_compiling.TryGetValue(data.ThreadID, out var method))
                {
                    method.Inlinees.Add(Signature(data.InlineeNamespace, data.InlineeName, data.InlineeNameSignature));
                }
            }
        };

        source.Clr.MethodILToNativeMap += data =>
        {
            lock (_gate)
            {
                var index = _methods.FindLastIndex(item => item.Compilation.MethodId == (ulong)data.MethodID);
                if (index >= 0)
                {
                    var publication = _methods[index];
                    // This field carries NativeCodeVersionID in the runtime's IL-to-native map event.
                    _methods[index] = publication with
                    {
                        Compilation = publication.Compilation with { CodeVersion = (ulong)data.ReJITID },
                    };
                }
            }
        };

        source.Clr.MethodLoadVerbose += data =>
        {
            if (!data.IsJitted)
            {
                return;
            }

            lock (_gate)
            {
                var inlinees = _compiling.TryGetValue(data.ThreadID, out var compiling) && compiling.Method == data.MethodID
                    ? compiling.Inlinees.ToArray() : [];
                _methods.Add(new NativeCodeEvent((ulong)data.ModuleID, data.MethodToken, "", new NativeCompilation
                {
                    Method = data.MethodNamespace + ":" + data.MethodName + " " + data.MethodSignature,
                    MethodId = (ulong)data.MethodID, Address = data.MethodStartAddress, CodeSize = data.MethodSize,
                    Tier = Tier(data.OptimizationTier), Inlinees = inlinees,
                }));
            }
        };
    }

    /// <summary>
    /// Retrieves immutable publication evidence for correlation or address normalization.
    /// </summary>
    /// <returns>The observed code versions in event order.</returns>
    internal NativeCodeEvent[] Snapshot()
    {
        lock (_gate)
        {
            return [.. _methods.Select(item => item with
        {
            Assembly = _modules.TryGetValue(item.ModuleId, out var assembly) && _assemblies.TryGetValue(assembly, out var name) ? name : "",
        })];
        }
    }

    /// <summary>
    /// Reports whether the exact selected method has published the requested tier.
    /// </summary>
    /// <param name="method">The worker's selected runtime identity.</param>
    /// <param name="identity">The selected metadata and generic arguments.</param>
    /// <param name="tier">The requested profile.</param>
    /// <returns>Whether sufficient publication evidence has arrived.</returns>
    internal bool Observed(ulong method, NativeMethodIdentity? identity, string tier)
    {
        return Snapshot().Any(item => Selected(item, method, identity) && tier switch
        {
            "tier1" => item.Compilation.Tier is "Tier1" or "OSR" or "Instrumented Tier1",
            "tier0" => item.Compilation.Tier is "Tier0" or "Instrumented Tier0",
            _ => true,
        });
    }

    /// <summary>
    /// Matches the exact descriptor or a shared generic body in the same observed module and metadata definition.
    /// </summary>
    /// <param name="publication">The observed compilation.</param>
    /// <param name="method">The selected descriptor.</param>
    /// <param name="identity">The selected original metadata.</param>
    /// <returns>Whether the publication can belong to the selected definition.</returns>
    internal static bool Selected(NativeCodeEvent publication, ulong method, NativeMethodIdentity? identity) =>
        publication.Compilation.MethodId == method || identity is not null
        && (identity.TypeArguments.Length != 0 || identity.MethodArguments.Length != 0)
        && publication.Token == identity.Token && publication.Assembly == identity.Assembly;

    private static string Tier(OptimizationTier tier) => tier switch
    {
        OptimizationTier.MinOptJitted => "MinOpts",
        OptimizationTier.Optimized => "FullOpts",
        OptimizationTier.QuickJitted => "Tier0",
        OptimizationTier.OptimizedTier1 => "Tier1",
        OptimizationTier.OptimizedTier1OSR => "OSR",
        OptimizationTier.QuickJittedInstrumented => "Instrumented Tier0",
        OptimizationTier.OptimizedTier1Instrumented => "Instrumented Tier1",
        _ => tier.ToString(),
    };

    private static string Signature(string owner, string name, string signature) => owner + ":" + name + " "
        + string.Join(' ', signature.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
