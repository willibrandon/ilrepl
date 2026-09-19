using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Compares authored metadata and method-body structure with an independent ECMA-335 reader.
/// </summary>
internal static class ExportMetadata
{
    /// <summary>
    /// Captures token-independent definitions, signatures, layouts, locals, and exception regions.
    /// </summary>
    /// <param name="image">The assembly produced by an exporter or independent assembler.</param>
    /// <returns>The sorted semantic metadata records.</returns>
    internal static string[] Read(byte[] image)
    {
        using var pe = new PEReader(new MemoryStream(image, writable: false));
        var reader = pe.GetMetadataReader();
        var provider = new ExportSignatureProvider();
        var records = new List<string>();
        foreach (var handle in reader.AssemblyReferences)
        {
            var reference = reader.GetAssemblyReference(handle);
            records.Add("reference " + reader.GetString(reference.Name) + " " + reference.Version + " "
                + Convert.ToHexString(reader.GetBlobBytes(reference.PublicKeyOrToken)) + " "
                + reader.GetString(reference.Culture) + " " + reference.Flags);
        }

        foreach (var handle in reader.TypeDefinitions)
        {
            var definition = reader.GetTypeDefinition(handle);
            var name = provider.GetTypeFromDefinition(reader, handle, 0);
            if (name == "<Module>")
            {
                continue;
            }

            var layout = definition.GetLayout();
            records.Add($"type {name} {definition.Attributes} {provider.Type(reader, definition.BaseType)}"
                + $" layout {layout.Size},{layout.PackingSize}");
            Generics(name, definition.GetGenericParameters());
            Attributes(name, definition.GetCustomAttributes());
            foreach (var implementation in definition.GetInterfaceImplementations())
            {
                records.Add(name + " implements " + provider.Type(reader, reader.GetInterfaceImplementation(implementation).Interface));
            }

            foreach (var implementationHandle in definition.GetMethodImplementations())
            {
                var implementation = reader.GetMethodImplementation(implementationHandle);
                records.Add(name + " override " + Method(implementation.MethodDeclaration) + " -> " + Method(implementation.MethodBody));
            }

            foreach (var propertyHandle in definition.GetProperties())
            {
                var property = reader.GetPropertyDefinition(propertyHandle);
                var propertyName = name + "::" + reader.GetString(property.Name);
                records.Add("property " + propertyName + " " + property.Attributes + " "
                    + ExportSignatureProvider.Method(property.DecodeSignature(provider, null)));
                var accessors = property.GetAccessors();
                records.Add(propertyName + " get " + Method(accessors.Getter) + " set " + Method(accessors.Setter));
                Constant(propertyName, property.GetDefaultValue());
                Attributes(propertyName, property.GetCustomAttributes());
            }

            foreach (var eventHandle in definition.GetEvents())
            {
                var value = reader.GetEventDefinition(eventHandle);
                var eventName = name + "::" + reader.GetString(value.Name);
                records.Add("event " + eventName + " " + value.Attributes + " " + provider.Type(reader, value.Type));
                var accessors = value.GetAccessors();
                records.Add(eventName + " add " + Method(accessors.Adder) + " remove " + Method(accessors.Remover)
                    + " raise " + Method(accessors.Raiser));
                Attributes(eventName, value.GetCustomAttributes());
            }

            foreach (var fieldHandle in definition.GetFields())
            {
                var field = reader.GetFieldDefinition(fieldHandle);
                var fieldName = name + "::" + reader.GetString(field.Name);
                records.Add($"field {fieldName} {field.Attributes} {field.DecodeSignature(provider, null)} offset {field.GetOffset()}");
                Constant(fieldName, field.GetDefaultValue());
                Attributes(fieldName, field.GetCustomAttributes());
            }

            foreach (var methodHandle in definition.GetMethods())
            {
                var method = reader.GetMethodDefinition(methodHandle);
                var methodName = name + "::" + reader.GetString(method.Name) + " "
                    + ExportSignatureProvider.Method(method.DecodeSignature(provider, null));
                records.Add($"method {methodName} {method.Attributes} {method.ImplAttributes}");
                Generics(methodName, method.GetGenericParameters());
                Attributes(methodName, method.GetCustomAttributes());
                foreach (var parameterHandle in method.GetParameters())
                {
                    var parameter = reader.GetParameter(parameterHandle);
                    var parameterName = methodName + " parameter " + parameter.SequenceNumber;
                    records.Add(parameterName + " " + parameter.Attributes + " " + reader.GetString(parameter.Name));
                    Constant(parameterName, parameter.GetDefaultValue());
                }

                if (method.RelativeVirtualAddress == 0)
                {
                    continue;
                }

                var body = pe.GetMethodBody(method.RelativeVirtualAddress);
                var locals = body.LocalSignature.IsNil ? []
                    : reader.GetStandaloneSignature(body.LocalSignature).DecodeLocalSignature(provider, null).ToArray();
                // ILDAsm omits an empty .locals declaration. Its init bit matters only for locals or localloc.
                var initializationMatters = locals.Length > 0 || ExportInstructions.HasLocalloc(body);
                records.Add(methodName + " locals " + (initializationMatters && body.LocalVariablesInitialized)
                    + " " + string.Join(',', locals));
                records.AddRange(ExportInstructions.Read(body, Token).Select(instruction => methodName + " " + instruction));
                foreach (var region in body.ExceptionRegions)
                {
                    records.Add(methodName + " handler " + region.Kind + " " + provider.Type(reader, region.CatchType));
                }
            }
        }

        return records.Order(StringComparer.Ordinal).ToArray();

        string Token(int token)
        {
            var handle = MetadataTokens.Handle(token);
            return handle.Kind switch
            {
                HandleKind.UserString => "string " + reader.GetUserString((UserStringHandle)handle),
                HandleKind.TypeDefinition or HandleKind.TypeReference or HandleKind.TypeSpecification
                    => provider.Type(reader, (EntityHandle)handle),
                HandleKind.MethodDefinition => Method((EntityHandle)handle),
                HandleKind.MethodSpecification => GenericMethod((MethodSpecificationHandle)handle),
                HandleKind.FieldDefinition => Field((FieldDefinitionHandle)handle),
                HandleKind.MemberReference => Member((MemberReferenceHandle)handle),
                HandleKind.StandaloneSignature => ExportSignatureProvider.Method(
                    reader.GetStandaloneSignature((StandaloneSignatureHandle)handle).DecodeMethodSignature(provider, null)),
                _ => throw new BadImageFormatException("Unexpected instruction token: " + handle.Kind),
            };
        }

        string GenericMethod(MethodSpecificationHandle handle)
        {
            var method = reader.GetMethodSpecification(handle);
            return Method(method.Method) + "<" + string.Join(',', method.DecodeSignature(provider, null)) + ">";
        }

        string Field(FieldDefinitionHandle handle)
        {
            var field = reader.GetFieldDefinition(handle);
            return provider.Type(reader, field.GetDeclaringType()) + "::" + reader.GetString(field.Name)
                + " " + field.DecodeSignature(provider, null);
        }

        string Member(MemberReferenceHandle handle)
        {
            var member = reader.GetMemberReference(handle);
            return member.GetKind() == MemberReferenceKind.Method ? Method(handle)
                : Parent(member.Parent) + "::" + reader.GetString(member.Name)
                    + " " + member.DecodeFieldSignature(provider, null);
        }

        string Method(EntityHandle handle)
        {
            if (handle.IsNil)
            {
                return "";
            }

            if (handle.Kind == HandleKind.MethodDefinition)
            {
                var method = reader.GetMethodDefinition((MethodDefinitionHandle)handle);
                return provider.Type(reader, method.GetDeclaringType()) + "::" + reader.GetString(method.Name)
                    + " " + ExportSignatureProvider.Method(method.DecodeSignature(provider, null));
            }

            var reference = reader.GetMemberReference((MemberReferenceHandle)handle);
            return Parent(reference.Parent) + "::" + reader.GetString(reference.Name)
                + " " + ExportSignatureProvider.Method(reference.DecodeMethodSignature(provider, null));
        }

        string Parent(EntityHandle handle) => handle.Kind switch
        {
            HandleKind.MethodDefinition => provider.Type(reader, reader.GetMethodDefinition((MethodDefinitionHandle)handle)
                .GetDeclaringType()),
            HandleKind.ModuleReference => reader.GetString(reader.GetModuleReference((ModuleReferenceHandle)handle).Name),
            _ => provider.Type(reader, handle),
        };

        void Generics(string owner, GenericParameterHandleCollection parameters)
        {
            foreach (var parameterHandle in parameters)
            {
                var parameter = reader.GetGenericParameter(parameterHandle);
                var constraints = parameter.GetConstraints().Select(constraint =>
                    provider.Type(reader, reader.GetGenericParameterConstraint(constraint).Type)).Order(StringComparer.Ordinal);
                records.Add(owner + " generic " + parameter.Index + " " + reader.GetString(parameter.Name)
                    + " " + parameter.Attributes + " " + string.Join(',', constraints));
            }
        }

        void Constant(string owner, ConstantHandle handle)
        {
            if (handle.IsNil)
            {
                return;
            }

            var constant = reader.GetConstant(handle);
            records.Add(owner + " constant " + constant.TypeCode + " " + Convert.ToHexString(reader.GetBlobBytes(constant.Value)));
        }

        void Attributes(string owner, CustomAttributeHandleCollection attributes)
        {
            foreach (var attributeHandle in attributes)
            {
                var attribute = reader.GetCustomAttribute(attributeHandle);
                var type = attribute.Constructor.Kind == HandleKind.MemberReference
                    ? reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent
                    : reader.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType();
                records.Add(owner + " attribute " + provider.Type(reader, type) + " "
                    + Convert.ToHexString(reader.GetBlobBytes(attribute.Value)));
            }
        }
    }
}
