using Mono.Cecil;

namespace IlRepl.Engine;

/// <summary>
/// Reuses the current module's core-library scope when reflection imports several framework signature types.
/// </summary>
/// <param name="module">The destination module that owns each imported reference.</param>
internal sealed class CecilReflectionImporter(ModuleDefinition module) : DefaultReflectionImporter(module)
{
    private IMetadataScope? _coreLibrary;

    /// <inheritdoc />
    protected override IMetadataScope ImportScope(Type type) => ReferenceEquals(type.Assembly, typeof(object).Assembly)
        ? _coreLibrary ??= base.ImportScope(type)
        : base.ImportScope(type);
}
