using System.Reflection;
using System.Runtime.Loader;
using IlRepl.Engine;
using Mono.Cecil;
using TypeAttributes = Mono.Cecil.TypeAttributes;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Writes a small assembly with Mono.Cecil and loads it from its bytes, so a test controls every
/// signature and instruction in it, exactly as the session's own definitions are produced.
/// </summary>
internal static class CecilFixture
{
    private static int s_counter;

    /// <summary>
    /// Builds and loads an assembly holding one public class <c>N.Fixture</c>.
    /// </summary>
    /// <param name="populate">Adds members to the class; the module is the first argument.</param>
    /// <param name="resolver">When given, the assembly is loaded through the resolver, which keeps its image for listings.</param>
    /// <returns>The loaded assembly, its bytes, and the fixture type.</returns>
    public static (Assembly Assembly, byte[] Image, Type Fixture) Build(Action<ModuleDefinition, TypeDefinition> populate, TypeResolver? resolver = null)
    {
        var name = "IlReplCecilFixture" + Interlocked.Increment(ref s_counter);
        using var definition = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition(name, new Version(1, 0, 0, 0)), name, ModuleKind.Dll);
        var module = definition.MainModule;
        module.ImportReference(typeof(object));
        var type = new TypeDefinition("N", "Fixture", TypeAttributes.Public | TypeAttributes.Class, module.TypeSystem.Object);
        module.Types.Add(type);
        populate(module, type);
        using var stream = new MemoryStream();
        definition.Write(stream);
        var image = stream.ToArray();
        var assembly = resolver is null
            ? new AssemblyLoadContext(name, isCollectible: false).LoadFromStream(new MemoryStream(image))
            : resolver.LoadImage(image);
        return (assembly, image, assembly.GetType("N.Fixture")!);
    }
}
