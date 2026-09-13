using System.Text.Json;
using IlRepl.Engine;
using IlRepl.Protocol;

namespace IlRepl.Host;

/// <summary>
/// The private host entry point for one disposable comparison execution.
/// </summary>
internal static class ComparisonWorkerProgram
{
    internal static async Task<int> RunAsync(string[] arguments)
    {
        if (arguments.Length != 6)
        {
            return 64;
        }

        var package = JsonSerializer.Deserialize(await File.ReadAllTextAsync(arguments[1]).ConfigureAwait(false),
            ProtocolJsonContext.Default.ComparisonPackage);
        if (package is null)
        {
            return 65;
        }

        var result = await ComparisonWorker.ExecuteAsync(package, arguments[2] == "original",
            () => File.WriteAllText(arguments[3], "ready"),
            () =>
            {
                File.WriteAllText(arguments[5], "output-limit");
                Environment.Exit(73);
            }).ConfigureAwait(false);
        await File.WriteAllTextAsync(arguments[4], JsonSerializer.Serialize(result,
            ProtocolJsonContext.Default.ComparisonSide)).ConfigureAwait(false);
        return 0;
    }
}
