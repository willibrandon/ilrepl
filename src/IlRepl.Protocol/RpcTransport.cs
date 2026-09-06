using System.Diagnostics.CodeAnalysis;
using StreamJsonRpc;

namespace IlRepl.Protocol;

/// <summary>
/// Builds the JSON-RPC message handler both sides use: header-delimited UTF-8 JSON with the
/// source-generated serializer.
/// </summary>
public static class RpcTransport
{
    /// <summary>
    /// Creates a message handler over a pair of streams.
    /// </summary>
    /// <param name="sending">The stream this side writes to.</param>
    /// <param name="receiving">The stream this side reads from.</param>
    /// <returns>The handler to pass to <see cref="JsonRpc"/>.</returns>
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "The formatter uses the source-generated JSON context.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "The formatter uses the source-generated JSON context.")]
    public static IJsonRpcMessageHandler CreateHandler(Stream sending, Stream receiving)
    {
        ArgumentNullException.ThrowIfNull(sending);
        ArgumentNullException.ThrowIfNull(receiving);
        var formatter = new SystemTextJsonFormatter
        {
            JsonSerializerOptions = { TypeInfoResolver = ProtocolJsonContext.Default },
        };
        return new HeaderDelimitedMessageHandler(sending, receiving, formatter);
    }
}
