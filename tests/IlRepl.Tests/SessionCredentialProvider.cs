using System.Text.Json;

namespace IlRepl.Tests;

/// <summary>
/// Supplies one isolated feed's credentials through the real NuGet executable plugin protocol.
/// </summary>
internal static class SessionCredentialProvider
{
    /// <summary>
    /// Runs the credential provider when NuGet launches this test executable as a plugin.
    /// </summary>
    /// <param name="args">The child process arguments.</param>
    /// <returns>Whether the process handled a credential provider invocation.</returns>
    internal static async Task<bool> TryRunAsync(string[] args)
    {
        var configuration = Environment.GetEnvironmentVariable("ILREPL_TEST_CREDENTIAL_PROVIDER");
        if (!args.Contains("-Plugin", StringComparer.OrdinalIgnoreCase) || configuration is null)
        {
            return false;
        }

        using var settings = JsonDocument.Parse(await File.ReadAllTextAsync(configuration));
        var root = settings.RootElement;
        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new
        {
            RequestId = "provider-handshake", Type = "Request", Method = "Handshake",
            Payload = new { ProtocolVersion = "2.0.0", MinimumProtocolVersion = "2.0.0" },
        }));

        while (await Console.In.ReadLineAsync() is { } line)
        {
            using var document = JsonDocument.Parse(line);
            var request = document.RootElement;
            if (request.GetProperty("Type").GetString() != "Request")
            {
                continue;
            }

            var method = request.GetProperty("Method").GetString();
            object payload = method switch
            {
                "Handshake" => new { ResponseCode = "Success", ProtocolVersion = "2.0.0" },
                "GetOperationClaims" => new { Claims = new[] { "Authentication" } },
                "GetAuthenticationCredentials" => new
                {
                    ResponseCode = "Success", Username = root.GetProperty("username").GetString(),
                    Password = root.GetProperty("password").GetString(), AuthenticationTypes = new[] { "basic" },
                },
                _ => new { ResponseCode = "Success" },
            };

            if (method == "GetAuthenticationCredentials")
            {
                var received = request.GetProperty("Payload");
                await File.WriteAllTextAsync(root.GetProperty("marker").GetString()!, received.GetRawText());
            }

            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new
            {
                RequestId = request.GetProperty("RequestId").GetString(), Type = "Response", Method = method, Payload = payload,
            }));

            if (method == "Close")
            {
                break;
            }
        }

        return true;
    }
}
