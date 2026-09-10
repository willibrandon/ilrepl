using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using StreamJsonRpc;

namespace IlRepl.Protocol;

/// <summary>
/// Supplies generated protocol metadata and the formatter's built-in converter for cancellation request identifiers.
/// </summary>
internal sealed class ProtocolTypeInfoResolver : IJsonTypeInfoResolver
{
    /// <summary>
    /// Resolves protocol types without reflection, including identifiers inside cancellation notifications.
    /// </summary>
    /// <param name="type">The type being serialized.</param>
    /// <param name="options">The formatter options, including its request-identifier converter.</param>
    /// <returns>The serialization metadata, or null for an unsupported type.</returns>
    public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options) => type == typeof(RequestId)
        ? JsonMetadataServices.CreateValueInfo<RequestId>(options,
            (JsonConverter<RequestId>)options.Converters.First(converter => converter.CanConvert(type)))
        : ((IJsonTypeInfoResolver)ProtocolJsonContext.Default).GetTypeInfo(type, options);
}
