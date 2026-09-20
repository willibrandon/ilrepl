using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Utilities;

namespace IlRepl.Tests;

/// <summary>
/// Gives tests that observe process-wide runtime state their own process while other tests continue in parallel.
/// </summary>
internal static class IsolatedTestProcess
{
    private const string SelectedTest = "ILREPL_ISOLATED_TEST";
    private const string Workspace = "ILREPL_ISOLATED_WORKSPACE";

    /// <summary>
    /// Runs only the calling test case in a child process, or lets its assertions run when already in that child.
    /// </summary>
    /// <param name="context">The calling test's cancellation and output context.</param>
    /// <param name="environment">Variables the child starts with, for a test whose subject reads them from its process.</param>
    /// <param name="method">The test method to run.</param>
    /// <returns>Whether the parent has completed the test in a child process.</returns>
    internal static async Task<bool> RunAsync(
        TestContext context,
        IReadOnlyDictionary<string, string>? environment = null,
        [CallerMemberName] string method = "")
    {
        var name = context.FullyQualifiedTestClassName + "." + method;
        if (Environment.GetEnvironmentVariable(SelectedTest) == name)
        {
            return false;
        }

        await RunChildAsync(context, name, environment: environment);
        return true;
    }

    /// <summary>
    /// Runs assertions with owned temporary files and deletes them only after the child releases all runtime image mappings.
    /// </summary>
    /// <param name="context">The calling test's cancellation and output context.</param>
    /// <param name="test">The assertions that use the parent's temporary directory.</param>
    /// <param name="method">The test method to run.</param>
    internal static async Task WithDirectoryAsync(TestContext context, Func<string, Task> test, [CallerMemberName] string method = "")
    {
        var name = context.FullyQualifiedTestClassName + "." + method;
        if (Environment.GetEnvironmentVariable(SelectedTest) == name)
        {
            await test(Environment.GetEnvironmentVariable(Workspace)
                ?? throw new InvalidOperationException("The isolated test workspace was not supplied."));
            return;
        }

        var directory = Directory.CreateTempSubdirectory("ilrepl-isolated-").FullName;
        try
        {
            await RunChildAsync(context, name, directory);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task RunChildAsync(
        TestContext context,
        string name,
        string? directory = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var start = new ProcessStartInfo
        {
            FileName = Environment.ProcessPath!,
            WorkingDirectory = AppContext.BaseDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        if (string.Equals(Path.GetFileNameWithoutExtension(start.FileName), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            start.ArgumentList.Add(typeof(IsolatedTestProcess).Assembly.Location);
        }

        var filter = "FullyQualifiedName=" + FilterHelper.Escape(name);
        if (context.TestData is { Length: > 0 })
        {
            var displayName = context.TestDisplayName
                ?? throw new InvalidOperationException("MSTest did not supply the data-row display name.");
            filter += "&Name=" + FilterHelper.Escape(displayName);
        }

        start.ArgumentList.Add("--filter");
        start.ArgumentList.Add(filter);
        start.Environment[SelectedTest] = name;
        if (directory is not null)
        {
            start.Environment[Workspace] = directory;
        }

        foreach (var (variable, value) in environment ?? new Dictionary<string, string>())
        {
            start.Environment[variable] = value;
        }

        using var child = Process.Start(start) ?? throw new InvalidOperationException("The isolated test did not start.");
        using var standardOutput = child.StandardOutput;
        using var standardError = child.StandardError;
        var output = standardOutput.ReadToEndAsync(context.CancellationToken);
        var error = standardError.ReadToEndAsync(context.CancellationToken);
        try
        {
            await child.WaitForExitAsync(context.CancellationToken);
            var details = await output + Environment.NewLine + await error;
            context.WriteLine(details);
            Assert.AreEqual(0, child.ExitCode, details);
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync(CancellationToken.None);
            }
        }
    }
}
