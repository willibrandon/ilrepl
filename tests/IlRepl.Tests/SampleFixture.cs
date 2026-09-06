using System.Diagnostics;

namespace IlRepl.Tests;

/// <summary>
/// Builds the sample projects once per test run and exposes their outputs.
/// </summary>
internal sealed class SampleFixture
{
    /// <summary>
    /// The built Greeter assembly.
    /// </summary>
    public string GreeterDll { get; private set; } = null!;

    /// <summary>
    /// Builds the samples that are missing.
    /// </summary>
    /// <returns>A task that completes when every sample is built.</returns>
    public async Task InitializeAsync()
    {
        GreeterDll = await BuildAsync("Greeter", "Greeter.dll").ConfigureAwait(false);
    }

    private static async Task<string> BuildAsync(string project, string assemblyFileName)
    {
        var projectDirectory = Path.Combine(RepoPaths.Root, "samples", project);
        var output = Path.Combine(projectDirectory, "bin", RepoPaths.Configuration, "net10.0", assemblyFileName);
        if (File.Exists(output))
        {
            return output;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = RepoPaths.Root,
        };
        foreach (var argument in new[] { "build", projectDirectory, "-c", RepoPaths.Configuration, "--nologo", "-v", "quiet" })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("dotnet did not start");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"building {project} failed:\n{await stdout.ConfigureAwait(false)}\n{await stderr.ConfigureAwait(false)}");
        }

        return File.Exists(output) ? output : throw new FileNotFoundException("sample output missing after build", output);
    }
}
