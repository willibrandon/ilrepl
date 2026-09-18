using System.Reflection;
using System.Runtime.InteropServices.JavaScript;

namespace IlRepl.Wasm;

/// <summary>
/// Executes generated independent conformance images in the browser validation build.
/// </summary>
public static partial class BrowserConformance
{
    /// <summary>
    /// Invokes the generated parameterless corpus entry point and returns its integer observation.
    /// </summary>
    /// <param name="image">The base64-encoded managed assembly image.</param>
    /// <param name="typeName">The fully qualified entry type.</param>
    /// <param name="methodName">The public static parameterless entry method.</param>
    /// <returns>The observed integer result.</returns>
    [JSExport]
    public static int Execute(string image, string typeName, string methodName)
    {
        var assembly = Assembly.Load(Convert.FromBase64String(image));
        var type = assembly.GetType(typeName, throwOnError: true)!;
        var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static, Type.EmptyTypes)
            ?? throw new MissingMethodException(typeName, methodName);
        return method.Invoke(null, null) is int result ? result
            : throw new InvalidOperationException("the conformance entry point did not return an integer");
    }
}
