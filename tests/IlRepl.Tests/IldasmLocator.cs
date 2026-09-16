using System.Reflection;

namespace IlRepl.Tests;

/// <summary>
/// Finds ildasm, the independent reader the disassembly listings are compared with.
/// </summary>
internal static class IldasmLocator
{
    /// <summary>
    /// The path of ildasm, or null when none is installed.
    /// </summary>
    /// <returns>The path.</returns>
    public static string? Find()
    {
        var name = OperatingSystem.IsWindows() ? "ildasm.exe" : "ildasm";
        if (Environment.GetEnvironmentVariable("ILREPL_ILDASM") is { Length: > 0 } configured && File.Exists(configured))
        {
            return configured;
        }

        var package = typeof(IldasmLocator).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "IldasmPackagePath")?.Value;
        if (!string.IsNullOrEmpty(package) && File.Exists(Path.Combine(package, name)))
        {
            return Path.Combine(package, name);
        }

        var directories = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin"));
        return directories.Where(d => d.Length > 0).Select(d => Path.Combine(d, name)).FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// Returns ildasm or fails the test when the restored tool and fallback locations are missing.
    /// </summary>
    /// <returns>The path.</returns>
    public static string Require()
    {
        var ildasm = Find();
        if (ildasm is not null)
        {
            return ildasm;
        }

        Assert.Fail("ildasm is required but was not found in the restored ILDAsm package, on PATH, or in ~/.local/bin. "
            + "Run dotnet restore to restore the platform's ILDAsm package.");
        return "";
    }

    /// <summary>
    /// Runs ildasm over an assembly and returns its text.
    /// </summary>
    /// <param name="assemblyPath">The assembly.</param>
    /// <returns>The ILAsm text ildasm printed.</returns>
    public static string Disassemble(string assemblyPath)
    {
        var ildasm = Require();
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ildasm, [assemblyPath])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.AreEqual(0, process.ExitCode, error);
        return output;
    }
}
