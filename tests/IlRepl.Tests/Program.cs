using System.Runtime.CompilerServices;
using IlRepl.Tests.EndToEnd;
using IlRepl.Tests.Engine;
using IlRepl.Tests.Responsiveness;
using IlRepl.Tests.Tui;
using Microsoft.Testing.Platform.Builder;
using CodeCoverageHook = Microsoft.Testing.Extensions.CodeCoverage.TestingPlatformBuilderHook;
using MSBuildHook = Microsoft.Testing.Platform.MSBuild.TestingPlatformBuilderHook;
using MSTestHook = Microsoft.VisualStudio.TestTools.UnitTesting.TestingPlatformBuilderHook;
using TelemetryHook = Microsoft.Testing.Extensions.Telemetry.TestingPlatformBuilderHook;
using TrxReportHook = Microsoft.Testing.Extensions.TrxReport.TestingPlatformBuilderHook;

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
    public static Task<int> Main(string[] args)
    {
        if (args is ["--console-startup-probe", var mode, var directory])
        {
            return RunStatusProbeAsync(() => ConsoleStartupProbe.RunAsync(mode, directory));
        }

        if (args is ["--windows-console", ..])
        {
            return RunStatusProbeAsync(() => WindowsConsoleProbe.RunAsync(args));
        }

        if (args is ["--responsiveness-measure", ..])
        {
            return RunProbeAsync(() => ResponsivenessProbe.TryRunAsync(args));
        }

        if (args is ["--packaged-smoke", _])
        {
            return RunProbeAsync(() => PackagedSmoke.TryRunAsync(args));
        }

        if (args is ["--export-browser-corpus", _])
        {
            return RunProbeAsync(() => ExportBrowserCorpus.TryRunAsync(args));
        }

        if (args is ["--export-probe", _, _] or ["--export-tool-output"] or ["--export-tool-wait", _])
        {
            return RunProbeAsync(() => ExportProbe.TryRunAsync(args));
        }

        if (args.Contains("-Plugin", StringComparer.OrdinalIgnoreCase)
            && Environment.GetEnvironmentVariable("ILREPL_TEST_CREDENTIAL_PROVIDER") is not null)
        {
            return RunProbeAsync(() => SessionCredentialProvider.TryRunAsync(args));
        }

        if (Environment.GetEnvironmentVariable(HistoryProbes.Probe) is "hold" or "append" or "path")
        {
            return RunProbeAsync(HistoryProbes.TryRunAsync);
        }

        if (Environment.GetEnvironmentVariable("ILREPL_ACTIVATION_PROBE_PATH") is not null)
        {
            return RunProbeAsync(ActivationEditTests.TryRunFileActivationProbeAsync);
        }

        if (Environment.GetEnvironmentVariable("ILREPL_DESCENDANT_RECORD") is not null)
        {
            return RunProbeAsync(ComparisonDescendantTests.TryRunDescendantProbeAsync);
        }

        return RunTestsAsync(args);
    }

    private static async Task<int> RunProbeAsync(Func<Task<bool>> probe)
    {
        try
        {
            await probe();
            return 0;
        }
        catch (Exception exception) when (exception is not (StackOverflowException or AccessViolationException))
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static async Task<int> RunStatusProbeAsync(Func<Task<int>> probe)
    {
        try
        {
            return await probe();
        }
        catch (Exception exception) when (exception is not (StackOverflowException or AccessViolationException))
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    // Keep test-platform assembly loading and JIT work out of short-lived private probe processes.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<int> RunTestsAsync(string[] args)
    {
        var builder = await TestApplication.CreateBuilderAsync(args);
        MSBuildHook.AddExtensions(builder, args);
        TelemetryHook.AddExtensions(builder, args);
        MSTestHook.AddExtensions(builder, args);
        TrxReportHook.AddExtensions(builder, args);
        CodeCoverageHook.AddExtensions(builder, args);
        using var application = await builder.BuildAsync();
        return await application.RunAsync();
    }
}
