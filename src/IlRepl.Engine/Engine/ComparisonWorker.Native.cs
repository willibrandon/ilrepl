using System.Reflection;
using System.Runtime.InteropServices;
using IlRepl.Protocol;

namespace IlRepl.Engine;

/// <summary>
/// Executes one captured side in a process or browser worker dedicated to that single execution.
/// </summary>
public static partial class ComparisonWorker
{
    private static Dictionary<string, string> MaterializeNativeLibraries(ComparisonImage image)
    {
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var library in image.NativeLibraries)
        {
            if (OperatingSystem.IsBrowser())
            {
                throw new ReplException("native dependencies cannot run in the browser; open this session in terminal ilrepl");
            }

            if (string.IsNullOrWhiteSpace(library.Name) || library.Name is "." or ".."
                || library.Name.Contains('/') || library.Name.Contains('\\') || library.Name.Contains(':')
                || library.Image.Length > SessionCodec.FileLimit || SessionCodec.Hash(library.Image) != library.Hash)
            {
                throw new ReplException("the comparison contains an invalid native dependency image");
            }

            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ilrepl", "native", library.Hash);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, library.Name);
            var valid = false;
            if (File.Exists(path))
            {
                using var existing = File.OpenRead(path);
                if (existing.Length == library.Image.Length)
                {
                    var bytes = new byte[library.Image.Length];
                    existing.ReadExactly(bytes);
                    valid = SessionCodec.Hash(bytes) == library.Hash;
                }
            }

            if (!valid)
            {
                var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllBytes(temporary, library.Image);
                    File.Move(temporary, path, overwrite: true);
                }
                finally
                {
                    File.Delete(temporary);
                }
            }

            if (!paths.TryAdd(library.Name, path))
            {
                throw new ReplException("the comparison contains duplicate native library names");
            }
        }

        return paths;
    }

    private static void BindNativeLibraries(Assembly assembly, IReadOnlyDictionary<string, string> paths)
    {
        if (paths.Count == 0)
        {
            return;
        }

        NativeLibrary.SetDllImportResolver(assembly, (name, _, _) =>
        {
            foreach (var candidate in new[] { name, name + ".dll", name + ".so", "lib" + name + ".so", "lib" + name + ".dylib" })
            {
                if (paths.TryGetValue(candidate, out var path))
                {
                    return NativeLibrary.Load(path);
                }
            }

            return 0;
        });
    }
}
