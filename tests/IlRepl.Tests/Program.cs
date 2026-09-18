using IlRepl.Tests.EndToEnd;
using IlRepl.Tests.Engine;
using IlRepl.Tests.Responsiveness;
using IlRepl.Tests.Tui;
using Microsoft.Testing.Platform.Builder;

namespace IlRepl.Tests;

/// <summary>
/// Runs child probes directly and otherwise starts MSTest.
/// </summary>
internal static class Program
{
    /// <summary>
    /// Runs the requested child probe or the test application.
    /// </summary>
    /// <param name="args">The test application arguments.</param>
    /// <returns>The process exit code.</returns>
    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args is ["--windows-console", ..]) return await WindowsConsoleProbe.RunAsync(args);
            if (await ResponsivenessProbe.TryRunAsync(args) ||
                await PackagedSmoke.TryRunAsync(args) ||
                await ExportBrowserCorpus.TryRunAsync(args) ||
                await ExportProbe.TryRunAsync(args) ||
                await SessionCredentialProvider.TryRunAsync(args) ||
                await HistoryProbes.TryRunAsync() ||
                await ActivationEditTests.TryRunFileActivationProbeAsync() ||
                await ComparisonDescendantTests.TryRunDescendantProbeAsync())
            {
                return 0;
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }

        var builder = await TestApplication.CreateBuilderAsync(args);
        Microsoft.Testing.Platform.MSBuild.TestingPlatformBuilderHook.AddExtensions(builder, args);
        Microsoft.Testing.Extensions.Telemetry.TestingPlatformBuilderHook.AddExtensions(builder, args);
        Microsoft.VisualStudio.TestTools.UnitTesting.TestingPlatformBuilderHook.AddExtensions(builder, args);
        Microsoft.Testing.Extensions.TrxReport.TestingPlatformBuilderHook.AddExtensions(builder, args);
        Microsoft.Testing.Extensions.CodeCoverage.TestingPlatformBuilderHook.AddExtensions(builder, args);
        using var application = await builder.BuildAsync();
        return await application.RunAsync();
    }
}
