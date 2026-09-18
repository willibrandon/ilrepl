using System.Text;
using System.Text.Json;
using IlRepl.Engine;
using IlRepl.Protocol;

namespace IlRepl.Host;

/// <summary>
/// The private host entry point for one disposable comparison execution.
/// </summary>
internal static class ComparisonWorkerProgram
{
    /// <summary>
    /// Executes one prepared comparison in an isolated runtime with acknowledged process ownership.
    /// </summary>
    /// <param name="arguments">The prepared request, result, assembly, and ownership paths.</param>
    /// <returns>The conventional worker exit code.</returns>
    internal static async Task<int> RunAsync(string[] arguments)
    {
        if (arguments.Length != 9)
        {
            return 64;
        }

        OwnedProcessGroup.PrepareWorker();
        WorkerOwnerWatchdog.Start();
        await File.WriteAllTextAsync(arguments[6], "prepared").ConfigureAwait(false);
        while (!File.Exists(arguments[7]))
        {
            await Task.Delay(10).ConfigureAwait(false);
        }

        Console.OutputEncoding = new UTF8Encoding(false);
        Console.InputEncoding = new UTF8Encoding(false);
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
            }, captureOutput: false, useStandardInput: true).ConfigureAwait(false);
        await File.WriteAllTextAsync(arguments[4], JsonSerializer.Serialize(result,
            ProtocolJsonContext.Default.ComparisonSide)).ConfigureAwait(false);
        await File.WriteAllTextAsync(arguments[8], "ready").ConfigureAwait(false);
        await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
        return 0;
    }
}
