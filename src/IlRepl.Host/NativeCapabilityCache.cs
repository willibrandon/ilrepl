using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IlRepl.Protocol;

namespace IlRepl.Host;

/// <summary>
/// Retains successful capability probes for the same installed runtime and worker configuration.
/// </summary>
internal static class NativeCapabilityCache
{
    private static readonly ConcurrentDictionary<string, NativeReply> Reports = new(StringComparer.Ordinal);

    /// <summary>
    /// Produces a non-secret cache identity without retaining the caller's environment values.
    /// </summary>
    /// <param name="package">The explicitly requested capability settings.</param>
    /// <returns>The configuration fingerprint.</returns>
    internal static string Key(NativePackage package)
    {
        var environment = package.Environment.Where(pair => pair.Key.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase)
            || pair.Key.StartsWith("COMPlus_", StringComparison.OrdinalIgnoreCase)).OrderBy(pair => pair.Key, StringComparer.Ordinal);
        var identity = typeof(object).Assembly.Location + ":" + typeof(object).Module.ModuleVersionId
            + ":" + RuntimeInformation.ProcessArchitecture + ":" + typeof(NativeCapabilityCache).Module.ModuleVersionId
            + ":" + JsonSerializer.Serialize(package.Options, ProtocolJsonContext.Default.NativeOptions)
            + ":" + string.Join('\n', environment.Select(pair => pair.Key + "=" + pair.Value));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    /// <summary>
    /// Finds a completed probe without coupling concurrent callers' cancellation lifetimes.
    /// </summary>
    /// <param name="key">The runtime and configuration fingerprint.</param>
    /// <param name="reply">The successful cached evidence.</param>
    /// <returns>Whether a completed matching probe exists.</returns>
    internal static bool TryGet(string key, [NotNullWhen(true)] out NativeReply? reply) =>
        Reports.TryGetValue(key, out reply);

    /// <summary>
    /// Caches successful evidence while bounding retained configuration variants.
    /// </summary>
    /// <param name="key">The configuration fingerprint.</param>
    /// <param name="reply">The completed probe.</param>
    internal static void Store(string key, NativeReply reply)
    {
        if (reply.Outcome != "complete")
        {
            return;
        }

        if (Reports.Count >= 32 && Reports.Keys.FirstOrDefault() is { } oldest)
        {
            Reports.TryRemove(oldest, out _);
        }

        Reports[key] = reply;
    }
}
