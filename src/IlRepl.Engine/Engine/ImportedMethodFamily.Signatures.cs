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
            foreach (var field in type.GetFields(Declared))
            {
                ScanSignature(RuntimeMetadataSignatures.Read(field), type, field + ": field signature");
            }

            foreach (var property in type.GetProperties(Declared).Where(property => property.GetAccessors(true).Any(_methods.ContainsKey)))
            {
                var (signature, _) = ImportedMetadata.PropertySignature(property);
                ScanSignature(signature.ReturnType, type, property + ": property signature");
                foreach (var parameter in signature.ParameterTypes)
                {
                    ScanSignature(parameter, type, property + ": index parameter");
                }
            }
        }
    }

    private void ScanMethodSignature(MethodBase method, MethodEditBody? body)
    {
        var location = MemberResolver.Describe(method);
        var owner = method.DeclaringType!;
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
        if (signature.Resolved is { } type)
        {
            ScanSignatureType(type, owner, location);
        }

        if (signature.Element is { } element)
        {
            ScanSignature(element, owner, location);
        }

        if (signature.Modifier is { } modifier)
        {
            ScanSignature(modifier, owner, location);
        }

        foreach (var argument in signature.Arguments)
        {
            ScanSignature(argument, owner, location);
        }

        if (signature.Method is { } method)
        {
            ScanSignature(method.ReturnType, owner, location);
            foreach (var parameter in method.Parameters)
            {
                ScanSignature(parameter, owner, location);
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
