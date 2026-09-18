using Mono.Cecil;

namespace IlRepl.Engine;

/// <summary>
/// Creates a reflection importer whose cached scope belongs to exactly one module.
/// </summary>
internal sealed class CecilReflectionImporterProvider : IReflectionImporterProvider
{
    /// <inheritdoc />
    public IReflectionImporter GetReflectionImporter(ModuleDefinition module) => new CecilReflectionImporter(module);
}
