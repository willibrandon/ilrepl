using System.Reflection;
using System.Reflection.Metadata;

namespace IlRepl.Engine;

/// <summary>
/// Opens a metadata reader over the metadata section of a loaded assembly. Both runtimes expose
/// the section for any non-dynamic assembly; the reader is valid for as long as the assembly stays
/// loaded, which a command's lifetime never outlives.
/// </summary>
public static class ModuleMetadata
{
    /// <summary>
    /// Opens a reader over a module's metadata.
    /// </summary>
    /// <param name="module">The module.</param>
    /// <returns>The reader, or null for a dynamic module or when the runtime has no metadata section to offer.</returns>
    public static unsafe MetadataReader? TryOpen(Module module)
    {
        ArgumentNullException.ThrowIfNull(module);
        if (module.Assembly.IsDynamic)
        {
            return null;
        }

        try
        {
            return module.Assembly.TryGetRawMetadata(out var blob, out var length) ? new MetadataReader(blob, length) : null;
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or BadImageFormatException or ArgumentException)
        {
            return null;
        }
    }
}
