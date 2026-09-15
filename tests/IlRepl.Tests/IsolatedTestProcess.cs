using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace IlRepl.Tests;

/// <summary>
/// Gives tests that observe process-wide runtime state their own process while other tests continue in parallel.
/// </summary>
internal static class IsolatedTestProcess
{
    private const string SelectedTest = "ILREPL_ISOLATED_TEST";

    /// <summary>
    /// Runs the calling test in a child process, or lets its assertions run when already in that child.
    /// </summary>
    /// <param name="context">The calling test's cancellation and output context.</param>
    /// <param name="method">The test method to run.</param>
    /// <returns>Whether the parent has completed the test in a child process.</returns>
    internal static async Task<bool> RunAsync(TestContext context, [CallerMemberName] string method = "")
    {
        var name = context.FullyQualifiedTestClassName + "." + method;
        if (Environment.GetEnvironmentVariable(SelectedTest) == name)
        {
            return false;
        }

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

        start.ArgumentList.Add("--filter");
        start.ArgumentList.Add("FullyQualifiedName=" + name);
        start.Environment[SelectedTest] = name;
        using var child = Process.Start(start) ?? throw new InvalidOperationException("The isolated test did not start.");
        var output = child.StandardOutput.ReadToEndAsync(context.CancellationToken);
        var error = child.StandardError.ReadToEndAsync(context.CancellationToken);
        try
        {
            await child.WaitForExitAsync(context.CancellationToken);
            var details = await output + Environment.NewLine + await error;
            context.WriteLine(details);
            Assert.AreEqual(0, child.ExitCode, details);
            return true;
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
