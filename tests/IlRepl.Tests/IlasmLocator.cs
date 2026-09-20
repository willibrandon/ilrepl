using System.Diagnostics;
using System.Reflection;

namespace IlRepl.Tests;

/// <summary>
/// Finds a compatible ilasm for assembling rendered IL.
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

        var package = typeof(IlasmLocator).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "IlasmPackagePath")?.Value;
        if (!string.IsNullOrEmpty(package) && File.Exists(Path.Join(package, name)))
        {
            return Path.Join(package, name);
        }

        var directories = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Append(Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin"));
        return directories.Where(d => d.Length > 0).Select(d => Path.Join(d, name)).FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// Returns ilasm or fails the test when the restored tool and fallback locations are missing.
    /// </summary>
    public static string Require()
    {
        var ilasm = Find();
        if (ilasm is not null)
        {
            return ilasm;
        }

        Assert.Fail("ilasm is required but was not found in the restored ILAsm package, on PATH, or in ~/.local/bin. "
            + "Run dotnet restore to restore the platform's ILAsm package.");
        return "";
    }

    /// <summary>
    /// Assembles ILAsm text into a library and returns its bytes.
    /// </summary>
    /// <param name="source">The ILAsm text.</param>
    /// <returns>The assembled image.</returns>
    public static byte[] Assemble(string source)
    {
        var result = AssembleResultAsync(source).GetAwaiter().GetResult();
        Assert.AreEqual(0, result.Tool.ExitCode, result.Tool.StandardOutput + result.Tool.StandardError + "\n" + source);
        Assert.IsNotEmpty(result.Image, "A successful assembler must produce an image.");
        return result.Image;
    }

    /// <summary>
    /// Runs the assembler without treating an expected nonzero exit as an infrastructure failure.
    /// </summary>
    /// <param name="source">The source to assemble, including intentional rejection controls.</param>
    /// <param name="cancellationToken">Cancels assembly and drains the terminated tool.</param>
    /// <returns>The exit code, diagnostics, and any successful assembly image.</returns>
    internal static async Task<IlasmResult> AssembleResultAsync(string source, CancellationToken cancellationToken = default)
    {
        var directory = Directory.CreateTempSubdirectory("ilrepl-ilasm-").FullName;
        try
        {
            var il = Path.Join(directory, "cell.il");
            var dll = Path.Join(directory, "cell.dll");
            await File.WriteAllTextAsync(il, source, cancellationToken);
            var start = new ProcessStartInfo(Require(), ["-DLL", "-QUIET", "-OUTPUT=" + dll, il])
            {
                WorkingDirectory = directory,
            };

            var result = await ToolProcess.RunAsync(start, cancellationToken);
            var image = result.ExitCode == 0 ? await File.ReadAllBytesAsync(dll, cancellationToken) : [];
            return new IlasmResult(result, image);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
