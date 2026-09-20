using System.Reflection;
using System.Runtime.Loader;

namespace IlRepl.Tests;

/// <summary>
/// Loads assembly images that tests hold as bytes.
/// </summary>
internal static class AssemblyImages
{
    /// <summary>
    /// Loads an image into a context and closes the stream that carried it, which the context has copied by then.
    /// </summary>
    /// <param name="context">The context that receives the assembly.</param>
    /// <param name="image">The complete assembly image.</param>
    /// <returns>The loaded assembly.</returns>
    internal static Assembly LoadImage(this AssemblyLoadContext context, byte[] image)
    {
        using var stream = new MemoryStream(image, writable: false);
        return context.LoadFromStream(stream);
    }
}
