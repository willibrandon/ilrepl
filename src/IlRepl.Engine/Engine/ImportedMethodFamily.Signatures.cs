using System.Reflection;
using IlRepl.Engine.Binding;

namespace IlRepl.Engine;

/// <summary>
/// Discovers all types in emitted member signatures, including nested modifiers and function pointers.
/// </summary>
internal sealed partial class ImportedMethodFamily
{
    private void ScanSignatures()
    {
        foreach (var (method, body) in _methods.ToArray())
        {
            ScanMethodSignature(method, body);
        }

        foreach (var type in _types.Keys.ToArray())
        {
            foreach (var contract in ImportedMetadata.Interfaces(type))
            {
                ConsiderType(contract, type);
            }

            foreach (var field in type.GetFields(Declared))
            {
                ScanSignature(RuntimeMetadataSignatures.Read(field), type, field + ": field signature");
            }

            foreach (var property in type.GetProperties(Declared).Where(property => ImportedMetadata.Accessors(property)
                .Any(_methods.ContainsKey)))
            {
                var (signature, _) = ImportedMetadata.PropertySignature(property);
                var location = TypeNameFormatter.Pretty(type) + "::" + property.Name;
                ScanSignature(signature.ReturnType, type, location + ": property signature");
                foreach (var parameter in signature.ParameterTypes)
                {
                    ScanSignature(parameter, type, location + ": index parameter");
                }

                if (ImportedMetadata.Accessors(property, otherOnly: true).Any(_methods.ContainsKey))
                {
                    // Reflection on Mono cannot expose a property containing only Other associations.
                    if (property.GetGetMethod(true) is { } getter)
                    {
                        AddMethod(getter, metadataOnly: true);
                    }

                    if (property.GetSetMethod(true) is { } setter)
                    {
                        AddMethod(setter, metadataOnly: true);
                    }
                }
            }

            foreach (var entry in type.GetEvents(Declared).Where(entry => ImportedMetadata.Accessors(entry).Any(_methods.ContainsKey)))
            {
                ScanSignature(ImportedMetadata.EventSignature(entry), type, TypeNameFormatter.Pretty(type) + "::" + entry.Name);
                // An event requires both registration methods, even when only another accessor was selected.
                if (entry.GetAddMethod(true) is { } add)
                {
                    AddMethod(add, metadataOnly: true);
                }

                if (entry.GetRemoveMethod(true) is { } remove)
                {
                    AddMethod(remove, metadataOnly: true);
                }
            }
        }
    }

    private void ScanMethodSignature(MethodBase method, MethodEditBody? body)
    {
        var location = MemberResolver.Describe(method);
        var owner = ContextOf(method);
        if (body is null)
        {
            var metadata = RuntimeMetadataSignatures.Read(method);
            ScanSignature(metadata.ReturnType, owner, location + ": return signature");
            foreach (var (parameter, index) in metadata.Parameters.Select((parameter, index) => (parameter, index)))
            {
                ScanSignature(parameter, owner, location + ": parameter " + index);
            }

            return;
        }

        var signature = body.State.Signature!;
        var symbol = signature.ExactSymbol ?? RuntimeSymbolImporter.Import(signature, null, DefinitionId.None,
            MethodSymbolSource.Declared, true);
        ScanSignature(SignatureSymbolIdentity.AnnotatedReturn(symbol), owner, location + ": return signature");
        foreach (var (parameter, index) in symbol.Parameters.Select((parameter, index) => (parameter, index)))
        {
            ScanSignature(SignatureSymbolIdentity.Annotated(parameter), owner, location + ": parameter " + index);
        }
    }

    private void ScanSignature(TypeSymbol signature, Type owner, string location)
    {
        foreach (var type in RuntimeSymbolTypes.Materialized(signature).Distinct())
        {
            ScanSignatureType(type, owner, location);
        }
    }

    private void ScanSignature(IlSignature signature, Type owner, string location)
    {
        foreach (var type in SignatureTypes(signature))
        {
            ScanSignatureType(type, owner, location);
        }
    }

    private static IEnumerable<Type> SignatureTypes(IlSignature signature)
    {
        if (signature.Resolved is { } type)
        {
            yield return type;
        }

        if (signature.Element is { } element)
        {
            foreach (var nested in SignatureTypes(element)) yield return nested;
        }

        if (signature.Modifier is { } modifier)
        {
            foreach (var nested in SignatureTypes(modifier)) yield return nested;
        }

        foreach (var argument in signature.Arguments)
        {
            foreach (var nested in SignatureTypes(argument)) yield return nested;
        }

        if (signature.Method is { } method)
        {
            foreach (var nested in SignatureTypes(method.ReturnType)) yield return nested;
            foreach (var parameter in method.Parameters)
            {
                foreach (var nested in SignatureTypes(parameter)) yield return nested;
            }
        }
    }

    private void ScanSignatureType(Type type, Type owner, string location)
    {
        ConsiderType(type, owner);
        if (!type.IsGenericParameter && TypeParser.PrimitiveKeyword(type) is null)
        {
            ReportType(type, location);
        }
    }
}
