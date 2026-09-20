using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using Mono.Cecil;
using ModuleReference = Mono.Cecil.ModuleReference;
using PropertySignature = System.Reflection.Metadata.MethodSignature<IlRepl.Engine.IlSignature>;

namespace IlRepl.Engine;

/// <summary>
/// Reads imported metadata that reflection cannot reproduce without executing user code.
/// </summary>
internal static partial class ImportedMetadata
{
    /// <summary>
    /// Decodes a property's exact signature from its metadata blob.
    /// </summary>
    /// <param name="property">The property to read.</param>
    /// <returns>The decoded signature and the length of its encoded blob in bytes.</returns>
    internal static (PropertySignature Signature, int Size) PropertySignature(PropertyInfo property)
    {
        var metadata = ModuleMetadata.TryOpen(property.Module)
            ?? throw new ReplException($"property metadata for {property.Name} is unavailable");
        var handle = MetadataTokens.PropertyDefinitionHandle(property.MetadataToken & 0x00ffffff);
        var provider = new MetadataSignatureProvider(token => property.Module.ResolveType(token));
        var definition = metadata.GetPropertyDefinition(handle);
        return (definition.DecodeSignature(provider, GenericContext.Empty), metadata.GetBlobBytes(definition.Signature).Length);
    }

    /// <summary>
    /// Lists the explicit method overrides a type declares in its metadata.
    /// </summary>
    /// <param name="owner">The type whose method implementation rows are read.</param>
    /// <returns>Each overriding body paired with the declaration it implements, closed over the owner's generic arguments.</returns>
    internal static IEnumerable<(MethodBase Body, MethodBase Declaration)> Overrides(Type owner)
    {
        var metadata = ModuleMetadata.TryOpen(owner.Module)
            ?? throw new ReplException($"override metadata for {owner.Name} is unavailable");
        var handle = MetadataTokens.TypeDefinitionHandle(owner.MetadataToken & 0x00ffffff);
        foreach (var row in metadata.GetTypeDefinition(handle).GetMethodImplementations()
            .Select(implementation => metadata.GetMethodImplementation(implementation)))
        {
            var arguments = owner.GetGenericArguments();
            yield return (owner.Module.ResolveMethod(MetadataTokens.GetToken(row.MethodBody), arguments, null)!,
                owner.Module.ResolveMethod(MetadataTokens.GetToken(row.MethodDeclaration), arguments, null)!);
        }
    }

    /// <summary>
    /// Rebuilds a method's platform invoke record against the module being written.
    /// </summary>
    /// <param name="method">The imported method declared with a native import.</param>
    /// <param name="writer">The writer whose module gains a reference to the native library when it lacks one.</param>
    /// <returns>The import attributes, the entry point name, and the module reference for the library.</returns>
    internal static PInvokeInfo NativeImport(MethodBase method, CecilWriter writer)
    {
        var metadata = ModuleMetadata.TryOpen(method.Module)
            ?? throw new ReplException($"native import metadata for {method.Name} is unavailable");
        var handle = MetadataTokens.MethodDefinitionHandle(method.MetadataToken & 0x00ffffff);
        var import = metadata.GetMethodDefinition(handle).GetImport();
        var library = metadata.GetString(metadata.GetModuleReference(import.Module).Name);
        var module = writer.Module.ModuleReferences.FirstOrDefault(reference => reference.Name == library);
        if (module is null)
        {
            module = new ModuleReference(library);
            writer.Module.ModuleReferences.Add(module);
        }

        return new PInvokeInfo((PInvokeAttributes)import.Attributes, metadata.GetString(import.Name), module);
    }

    /// <summary>
    /// Reads the initial bytes of an RVA field from the image its module was loaded from.
    /// </summary>
    /// <param name="field">The field whose data lives in the image.</param>
    /// <param name="resolver">The resolver that may hold the retained image of the field's assembly.</param>
    /// <returns>The field's data, sized by its explicit layout or else by its marshaled size.</returns>
    internal static byte[] ReadFieldData(FieldInfo field, TypeResolver resolver)
    {
        byte[]? image = null;
        if (SessionAssemblies.TryGetDefinition(field.Module.Assembly, out var definition))
        {
            image = definition.Image;
        }

        if (image is null && resolver.TryGetImage(field.Module.Assembly, out var retained))
        {
            image = retained;
        }

        if (image is null && field.Module.Assembly.Location is { Length: > 0 } path && File.Exists(path))
        {
            image = File.ReadAllBytes(path);
        }

        if (image is null)
        {
            throw new ReplException($"the retained image for RVA field {field.DeclaringType}::{field.Name} is unavailable");
        }

        using var pe = new PEReader(ImmutableArray.Create(image));
        var reader = pe.GetMetadataReader();
        if (reader.GetGuid(reader.GetModuleDefinition().Mvid) != field.Module.ModuleVersionId)
        {
            throw new ReplException($"the image for RVA field {field.Name} no longer matches its loaded module");
        }

        var handle = (FieldDefinitionHandle)MetadataTokens.EntityHandle(field.MetadataToken);
        var data = reader.GetFieldDefinition(handle);
        var size = field.FieldType.StructLayoutAttribute?.Size ?? 0;
        if (size == 0)
        {
            size = Marshal.SizeOf(field.FieldType);
        }

        return pe.GetSectionData(data.GetRelativeVirtualAddress()).GetContent(0, size).ToArray();
    }
}
