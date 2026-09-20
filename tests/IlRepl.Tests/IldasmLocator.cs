using System.Diagnostics;
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
        if (!string.IsNullOrEmpty(package) && File.Exists(Path.Join(package, name)))
        {
            return Path.Join(package, name);
        }

        var directories = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Append(Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin"));
        return directories.Where(d => d.Length > 0).Select(d => Path.Join(d, name)).FirstOrDefault(File.Exists);
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
        var result = ToolProcess.RunAsync(new ProcessStartInfo(Require(), ["-UTF8", assemblyPath])).GetAwaiter().GetResult();
        Assert.AreEqual(0, result.ExitCode, result.StandardError);
        return result.StandardOutput;
    }

    /// <summary>
    /// Disassembles and reassembles an image using Microsoft's independent tools.
    /// </summary>
    /// <param name="image">The assembly produced by ilrepl.</param>
    /// <returns>The independently reconstructed image.</returns>
    internal static byte[] RoundTrip(byte[] image)
    {
        var directory = Directory.CreateTempSubdirectory("ilrepl-ildasm-").FullName;
        try
        {
            var path = Path.Join(directory, "cell.dll");
            File.WriteAllBytes(path, image);
            return IlasmLocator.Assemble(Disassemble(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
