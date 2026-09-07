using System.Reflection;

namespace IlRepl.Tests;

/// <summary>
/// Finds an ilasm to assemble rendered ILAsm with: the one named by ILREPL_ILASM, one on the
/// PATH or in ~/.local/bin, or the one restored with the test project's ILAsm package.
/// </summary>
internal static class IlasmLocator
{
    /// <summary>
    /// The path of ilasm, or null when none can be found.
    /// </summary>
    public static string? Find()
    {
        var name = OperatingSystem.IsWindows() ? "ilasm.exe" : "ilasm";
        if (Environment.GetEnvironmentVariable("ILREPL_ILASM") is { Length: > 0 } configured && File.Exists(configured))
        {
            return configured;
        }

        var directories = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin"))
            .ToList();
        var package = typeof(IlasmLocator).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "IlasmPackagePath")?.Value;
        if (!string.IsNullOrEmpty(package))
        {
            directories.Add(package);
        }

        return directories.Where(d => d.Length > 0).Select(d => Path.Combine(d, name)).FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// The path of ilasm. Without one the test is inconclusive, or fails when ILREPL_REQUIRE_ILASM is set, as on CI.
    /// </summary>
    public static string Require()
    {
        var ilasm = Find();
        if (ilasm is not null)
        {
            return ilasm;
        }

        if (Environment.GetEnvironmentVariable("ILREPL_REQUIRE_ILASM") is { Length: > 0 })
        {
            Assert.Fail("ilasm is required (ILREPL_REQUIRE_ILASM is set) but was not found on the PATH, in ~/.local/bin, or in the restored ILAsm package");
        }

        Assert.Inconclusive("ilasm is not installed");
        return "";
    }

    /// <summary>
    /// Assembles ILAsm text into a library and returns its bytes.
    /// </summary>
    /// <param name="source">The ILAsm text.</param>
    /// <returns>The assembled image.</returns>
    public static byte[] Assemble(string source)
    {
        var ilasm = Require();
        var directory = Path.Combine(Path.GetTempPath(), "ilrepl-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var il = Path.Combine(directory, "cell.il");
            var dll = Path.Combine(directory, "cell.dll");
            File.WriteAllText(il, source);
            // Options take a dash: a slash is a path on Unix.
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ilasm, ["-DLL", "-QUIET", "-OUTPUT=" + dll, il])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            })!;
            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.AreEqual(0, process.ExitCode, output + "\n" + source);
            return File.ReadAllBytes(dll);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
