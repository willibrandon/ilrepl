using System.Reflection;
using System.Reflection.Metadata.Ecma335;
using System.Runtime.InteropServices;
using Mono.Cecil;
using CecilFieldAttributes = Mono.Cecil.FieldAttributes;
using CecilGenericAttributes = Mono.Cecil.GenericParameterAttributes;
using CecilMethodAttributes = Mono.Cecil.MethodAttributes;
using CecilParameterAttributes = Mono.Cecil.ParameterAttributes;
using CecilTypeAttributes = Mono.Cecil.TypeAttributes;
using ReflectionFieldAttributes = System.Reflection.FieldAttributes;
using ReflectionGenericAttributes = System.Reflection.GenericParameterAttributes;
using ReflectionMethodAttributes = System.Reflection.MethodAttributes;
using ReflectionParameterAttributes = System.Reflection.ParameterAttributes;
using ReflectionTypeAttributes = System.Reflection.TypeAttributes;

namespace IlRepl.Engine;

/// <summary>
/// Reproduces declaring types, members, and their executable metadata.
/// </summary>
internal sealed partial class ImportedMethodFamily
{
    internal Dictionary<MemberInfo, IMemberDefinition> Write(CecilWriter writer)
    {
        RequireValid();
        foreach (var assembly in _externalTypes.Select(type => type.Assembly).Distinct())
        {
            writer.GrantAccessTo(assembly.GetName().Name!);
        }

        var definitions = new Dictionary<MemberInfo, IMemberDefinition>();
        foreach (var (original, path) in _types)
        {
            var slash = path.LastIndexOf('/');
            var dot = path.LastIndexOf('.');
            var attributes = (CecilTypeAttributes)original.Attributes;
            // A generated owner is addressable from the session. Member accessibility and
            // nested relationships remain as declared; making the root visible grants no member access.
            attributes = (attributes & ~CecilTypeAttributes.VisibilityMask) |
                (slash < 0 ? CecilTypeAttributes.Public
                    : (CecilTypeAttributes)(original.Attributes & ReflectionTypeAttributes.VisibilityMask));
            var name = path[(dot + 1)..];
            if (original.IsNested)
            {
                var metadata = ModuleMetadata.TryOpen(original.Module)!;
                var handle = MetadataTokens.TypeDefinitionHandle(original.MetadataToken & 0x00ffffff);
                name = metadata.GetString(metadata.GetTypeDefinition(handle).Name);
            }

            var definition = new TypeDefinition(original.IsNested ? "" : path[..dot], name, attributes);
            if (original.DeclaringType is { } parent)
            {
                ((TypeDefinition)definitions[DefinitionOf(parent)]).NestedTypes.Add(definition);
            }
            else
            {
                writer.Module.Types.Add(definition);
            }

            writer.Define(original, definition);
            definitions.Add(original, definition);
            if (original.IsGenericTypeDefinition)
            {
                DefineGenerics(original.GetGenericArguments(), definition, writer);
            }
        }

        var moduleOwners = DefineModuleOwners(writer);
        foreach (var original in _methods.Keys)
        {
            var definition = new MethodDefinition(original.Name, (CecilMethodAttributes)original.Attributes, writer.Module.TypeSystem.Void)
            {
                ImplAttributes = (Mono.Cecil.MethodImplAttributes)original.GetMethodImplementationFlags(),
                HasThis = !original.IsStatic,
                ExplicitThis = original.CallingConvention.HasFlag(CallingConventions.ExplicitThis),
                CallingConvention = original.CallingConvention.HasFlag(CallingConventions.VarArgs) ? MethodCallingConvention.VarArg
                    : MethodCallingConvention.Default,
            };

            var owner = IsModuleInitializer(original) ? moduleOwners[original.Module]
                : (TypeDefinition)definitions[DefinitionOf(original.DeclaringType!)];
            owner.Methods.Add(definition);
            writer.Define(original, definition);
            definitions.Add(original, definition);
            if (original.IsGenericMethodDefinition)
            {
                DefineGenerics(original.GetGenericArguments(), definition, writer);
            }
        }

        foreach (var original in _types.Keys)
        {
            var definition = (TypeDefinition)definitions[original];
            foreach (var field in original.GetFields(Declared))
            {
                var copy = new FieldDefinition(field.Name, (CecilFieldAttributes)field.Attributes, writer.Module.TypeSystem.Void);
                definition.Fields.Add(copy);
                writer.Define(field, copy);
                definitions.Add(field, copy);
            }
        }

        // Exported references that were bound to a committed copy resolve to these definitions,
        // including constructors, fields, and methods on constructed generic owner types.
        foreach (var (source, runtime) in _runtime)
        {
            switch (runtime)
            {
                case Type type:
                    writer.Define(type, (TypeDefinition)definitions[source]);
                    break;
                case MethodBase method:
                    writer.Define(method, (MethodDefinition)definitions[source]);
                    break;
                case FieldInfo field:
                    writer.Define(field, (FieldDefinition)definitions[source]);
                    break;
                default:
                    break;
            }
        }

        foreach (var (source, definition) in definitions)
        {
            switch (source)
            {
                case MethodBase method:
                    FillMethod(method, (MethodDefinition)definition, writer);
                    break;
                case FieldInfo field:
                    FillField(field, (FieldDefinition)definition, writer);
                    break;
                default:
                    break;
            }

            CopyAttributes(source.GetCustomAttributesData(), definition, writer);
            if (source is MethodBase edited && _methods[edited] is { } body)
            {
                WriteEditedAttributes(body, (MethodDefinition)definition, writer);
            }
        }

        // Constructed override references copy method signatures, so those signatures must be complete first.
        foreach (var type in _types.Keys)
        {
            FillType(type, (TypeDefinition)definitions[type], definitions, writer);
        }

        foreach (var pair in _methods)
        {
            if (pair.Value is { } body)
            {
                CecilBodyEmitter.Emit((MethodDefinition)definitions[pair.Key], body.State, writer, EmitMap.ForSessionMethods(_pinned));
            }
        }

        WriteTypeLookups(writer, definitions);
        WriteModuleInitializers(writer, definitions);
        var selected = (MethodDefinition)definitions[Selected.Method];
        if (!selected.IsPublic || !Selected.Method.DeclaringType!.IsVisible)
        {
            if (selected.CallingConvention == MethodCallingConvention.VarArg)
            {
                // Optional arguments belong to the caller's signature and cannot pass through a fixed forwarding body.
                // Session callers already receive access grants; standalone callers need the same access to their own image.
                writer.GrantAccessTo(writer.Name);
            }
            else
            {
                writer.GrantAccessTo(writer.Name);
                var forwarding = CecilForwardingMethod.Create(selected, ForwardingName);
                if (_forwardingMethod is { } runtime)
                {
                    DefineForwarding(writer, runtime, forwarding);
                }
            }
        }

        return definitions;
    }

    private static void DefineForwarding(CecilWriter writer, MethodBase runtime, MethodDefinition forwarding)
    {
        writer.Define(runtime, forwarding);
        writer.Define(runtime.DeclaringType!, forwarding.DeclaringType);
        foreach (var (source, parameter) in runtime.DeclaringType!.GetGenericArguments().Zip(forwarding.DeclaringType.GenericParameters))
        {
            writer.Define(source, parameter);
        }

        foreach (var (source, parameter) in runtime.GetGenericArguments().Zip(forwarding.GenericParameters))
        {
            writer.Define(source, parameter);
        }
    }

    private static void DefineGenerics(Type[] parameters, IGenericParameterProvider owner, CecilWriter writer)
    {
        foreach (var parameter in parameters)
        {
            var definition = new GenericParameter(parameter.Name, owner)
            {
                Attributes = (CecilGenericAttributes)parameter.GenericParameterAttributes,
            };

            owner.GenericParameters.Add(definition);
            writer.Define(parameter, definition);
        }
    }

    private static void FillGenerics(
        Type[] parameters,
        IGenericParameterProvider owner,
        CecilWriter writer,
        IReadOnlyList<GenericParameterDeclaration>? declarations = null)
    {
        for (var index = 0; index < parameters.Length; index++)
        {
            var declaration = declarations?[index];
            if (declaration is not null)
            {
                owner.GenericParameters[index].Name = declaration.Name;
                // Preserve runtime flags that the editable generic-parameter grammar cannot express.
                const ReflectionGenericAttributes editable = ReflectionGenericAttributes.VarianceMask
                    | ReflectionGenericAttributes.SpecialConstraintMask;
                owner.GenericParameters[index].Attributes = (CecilGenericAttributes)
                    ((parameters[index].GenericParameterAttributes & ~editable) | declaration.Attributes);
            }

            foreach (var constraint in declaration?.Constraints ?? parameters[index].GetGenericParameterConstraints())
            {
                owner.GenericParameters[index].Constraints.Add(new GenericParameterConstraint(writer.Import(constraint)));
            }

            CopyAttributes(parameters[index].GetCustomAttributesData(), owner.GenericParameters[index], writer);
        }
    }

    private void FillType(
        Type original,
        TypeDefinition definition,
        Dictionary<MemberInfo, IMemberDefinition> definitions,
        CecilWriter writer)
    {
        definition.BaseType = original.BaseType is { } parent ? writer.Import(parent) : null;
        if (original.StructLayoutAttribute is { } layout && layout.Value != LayoutKind.Auto)
        {
            definition.PackingSize = (short)layout.Pack;
            definition.ClassSize = layout.Size;
        }

        if (original.IsGenericTypeDefinition)
        {
            FillGenerics(original.GetGenericArguments(), definition, writer);
        }

        foreach (var contract in ImportedMetadata.Interfaces(original))
        {
            definition.Interfaces.Add(new InterfaceImplementation(writer.Import(contract)));
        }

        WriteOverrides(original, definitions, writer);

        foreach (var property in original.GetProperties(Declared))
        {
            var get = property.GetGetMethod(true);
            var set = property.GetSetMethod(true);
            var copiedGet = get is not null && definitions.TryGetValue(get, out var g) ? (MethodDefinition)g : null;
            var copiedSet = set is not null && definitions.TryGetValue(set, out var s) ? (MethodDefinition)s : null;
            var others = ImportedMetadata.Accessors(property, otherOnly: true).Where(definitions.ContainsKey)
                .Select(method => (MethodDefinition)definitions[method]).ToArray();
            if (copiedGet is null && copiedSet is null && others.Length == 0)
            {
                continue;
            }

            var (signature, size) = ImportedMetadata.PropertySignature(property);
            var copy = new PropertyDefinition(property.Name, (Mono.Cecil.PropertyAttributes)property.Attributes,
                writer.Object)
            {
                GetMethod = copiedGet,
                SetMethod = copiedSet,
                HasThis = signature.Header.IsInstance,
            };

            definition.Properties.Add(copy);
            foreach (var other in others)
            {
                copy.OtherMethods.Add(other);
            }

            copy.PropertyType = CecilMetadataSignatures.Import(signature.ReturnType, copy, writer);
            writer.SignatureFixups.Property(copy,
                signature.ParameterTypes.Select(parameter => CecilMetadataSignatures.Import(parameter, copy, writer)), size);

            if (copy.HasDefault)
            {
                copy.Constant = property.GetRawConstantValue();
            }

            CopyAttributes(property.GetCustomAttributesData(), copy, writer);
        }

        foreach (var @event in original.GetEvents(Declared))
        {
            var add = @event.GetAddMethod(true);
            var remove = @event.GetRemoveMethod(true);
            var copiedAdd = add is not null && definitions.TryGetValue(add, out var a) ? (MethodDefinition)a : null;
            var copiedRemove = remove is not null && definitions.TryGetValue(remove, out var r) ? (MethodDefinition)r : null;
            var raise = @event.GetRaiseMethod(true);
            var copiedRaise = raise is not null && definitions.TryGetValue(raise, out var fire) ? (MethodDefinition)fire : null;
            var others = ImportedMetadata.Accessors(@event, otherOnly: true).Where(definitions.ContainsKey)
                .Select(method => (MethodDefinition)definitions[method]).ToArray();
            if (copiedAdd is null && copiedRemove is null && copiedRaise is null && others.Length == 0)
            {
                continue;
            }

            var copy = new EventDefinition(@event.Name, (Mono.Cecil.EventAttributes)@event.Attributes,
                CecilMetadataSignatures.Import(ImportedMetadata.EventSignature(@event), definition, writer))
            {
                AddMethod = copiedAdd,
                RemoveMethod = copiedRemove,
                InvokeMethod = copiedRaise,
            };

            foreach (var other in others)
            {
                copy.OtherMethods.Add(other);
            }

            CopyAttributes(@event.GetCustomAttributesData(), copy, writer);
            definition.Events.Add(copy);
        }
    }

    private void FillMethod(MethodBase original, MethodDefinition definition, CecilWriter writer)
    {
        if (original.Attributes.HasFlag(ReflectionMethodAttributes.PinvokeImpl))
        {
            definition.PInvokeInfo = ImportedMetadata.NativeImport(original, writer);
        }

        var body = _methods[original];
        if (body is not null)
        {
            var signature = body.State.Signature!;
            definition.Attributes = (CecilMethodAttributes)signature.Attributes;
            definition.ImplAttributes = (Mono.Cecil.MethodImplAttributes)signature.ImplAttributes;
            definition.HasThis = !signature.IsStatic;
            definition.ExplicitThis = signature.CallingConvention.HasFlag(CallingConventions.ExplicitThis);
            definition.CallingConvention = signature.CallingConvention.HasFlag(CallingConventions.VarArgs)
                ? MethodCallingConvention.VarArg : MethodCallingConvention.Default;
            definition.ReturnType = writer.ImportSignature(signature.ReturnType, signature.ExactReturnType,
                signature.ReturnRequiredModifiers, signature.ReturnOptionalModifiers);
            for (var index = 0; index < signature.Parameters.Count; index++)
            {
                var parameter = signature.Parameters[index];
                definition.Parameters.Add(new ParameterDefinition(parameter.Name, (CecilParameterAttributes)parameter.Attributes,
                    writer.ImportSignature(parameter.Type, parameter.ExactType, parameter.RequiredModifiers, parameter.OptionalModifiers)));
            }
        }
        else
        {
            var signature = RuntimeMetadataSignatures.Read(original);
            definition.ReturnType = CecilMetadataSignatures.Import(signature.ReturnType, definition, writer);
            foreach (var parameter in signature.Parameters)
            {
                definition.Parameters.Add(new ParameterDefinition(CecilMetadataSignatures.Import(parameter, definition, writer)));
            }
        }

        var originalParameters = original.GetParameters();
        for (var index = 0; index < Math.Min(originalParameters.Length, definition.Parameters.Count); index++)
        {
            var parameter = originalParameters[index];
            var copy = definition.Parameters[index];
            if (body is null)
            {
                copy.Name = parameter.Name ?? copy.Name;
                copy.Attributes = (CecilParameterAttributes)parameter.Attributes;
            }
            else
            {
                const CecilParameterAttributes editable = CecilParameterAttributes.In | CecilParameterAttributes.Out
                    | CecilParameterAttributes.Optional;
                copy.Attributes |= (CecilParameterAttributes)parameter.Attributes & ~editable;
            }

            if (parameter.HasDefaultValue)
            {
                copy.Constant = parameter.RawDefaultValue;
            }

            if (parameter.Attributes.HasFlag(ReflectionParameterAttributes.HasFieldMarshal))
            {
                copy.MarshalInfo = ImportedMarshalling.Read(original.Module, ImportedMarshalling.ParameterToken(parameter),
                    writer, _session.Resolver, copy);
            }

            CopyAttributes(parameter.GetCustomAttributesData(), copy, writer);
        }

        foreach (var entry in body?.State.Entries ?? [])
        {
            if (entry.Kind == EntryKind.Param && entry.ParamIndex is > 0 and { } index && entry.ParamHasDefault)
            {
                var parameter = definition.Parameters[index - 1];
                parameter.Constant = entry.ParamDefault;
                parameter.HasDefault = true;
            }
        }

        if (original is MethodInfo method)
        {
            definition.MethodReturnType.Attributes = (CecilParameterAttributes)ImportedMarshalling.Attributes(method.ReturnParameter);
            definition.MethodReturnType.Name = method.ReturnParameter.Name;
            if (method.ReturnParameter.HasDefaultValue)
            {
                definition.MethodReturnType.Constant = method.ReturnParameter.RawDefaultValue;
            }

            if (definition.MethodReturnType.Attributes.HasFlag(CecilParameterAttributes.HasFieldMarshal))
            {
                definition.MethodReturnType.MarshalInfo = ImportedMarshalling.Read(original.Module,
                    ImportedMarshalling.ParameterToken(method.ReturnParameter), writer, _session.Resolver, definition.MethodReturnType);
            }

            CopyAttributes(method.ReturnParameter.GetCustomAttributesData(), definition.MethodReturnType, writer);
        }

        if (original.IsGenericMethodDefinition)
        {
            FillGenerics(original.GetGenericArguments(), definition, writer, body?.State.Signature!.TypeParameters);
        }
    }

    private void FillField(FieldInfo original, FieldDefinition definition, CecilWriter writer)
    {
        if (original.Attributes.HasFlag(ReflectionFieldAttributes.HasFieldMarshal))
        {
            definition.MarshalInfo = ImportedMarshalling.Read(original.Module, original.MetadataToken, writer,
                _session.Resolver, definition);
        }

        definition.FieldType = CecilMetadataSignatures.Import(RuntimeMetadataSignatures.Read(original), definition, writer);
        if (original.IsLiteral)
        {
            definition.Constant = original.GetRawConstantValue();
        }

        if (original.GetCustomAttributesData().FirstOrDefault(a => a.AttributeType == typeof(FieldOffsetAttribute)) is { } offset)
        {
            definition.Offset = (int)offset.ConstructorArguments[0].Value!;
        }

        if (original.Attributes.HasFlag(ReflectionFieldAttributes.HasFieldRVA))
        {
            // An RVA field is immutable module data, not a static value: reading it through
            // FieldInfo.GetValue would run the source initializer during import.
            definition.InitialValue = ImportedMetadata.ReadFieldData(original, _session.Resolver);
        }
    }

    private static void CopyAttributes(
        IList<CustomAttributeData> attributes,
        Mono.Cecil.ICustomAttributeProvider target,
        CecilWriter writer)
    {
        foreach (var attribute in attributes)
        {
            // These are projections of metadata flags/tables and are emitted in those tables.
            if (IsProjectedAttribute(attribute.AttributeType))
            {
                continue;
            }

            var copy = new CustomAttribute(writer.Import(attribute.Constructor));
            foreach (var (argument, parameter) in attribute.ConstructorArguments.Zip(attribute.Constructor.GetParameters()))
            {
                copy.ConstructorArguments.Add(AttributeArgument(argument, writer, parameter.ParameterType));
            }

            foreach (var argument in attribute.NamedArguments)
            {
                var named = new Mono.Cecil.CustomAttributeNamedArgument(argument.MemberName, AttributeArgument(argument.TypedValue,
                    writer, argument.MemberInfo is FieldInfo field ? field.FieldType : ((PropertyInfo)argument.MemberInfo).PropertyType));
                if (argument.IsField)
                {
                    copy.Fields.Add(named);
                }
                else
                {
                    copy.Properties.Add(named);
                }
            }

            target.CustomAttributes.Add(copy);
        }
    }

    private static CustomAttributeArgument AttributeArgument(
        CustomAttributeTypedArgument argument,
        CecilWriter writer,
        Type? declared = null)
    {
        var argumentType = argument.Value is Type ? typeof(Type) : argument.ArgumentType;
        var value = new CustomAttributeArgument(writer.Import(argumentType), argument.Value switch
        {
            Type type => writer.Import(type),
            IList<CustomAttributeTypedArgument> array => array.Select(item =>
                AttributeArgument(item, writer, argument.ArgumentType.GetElementType())).ToArray(),
            CustomAttributeTypedArgument nested => AttributeArgument(nested, writer),
            _ => argument.Value,
        });

        return declared == typeof(object) && argumentType != typeof(object)
            ? new CustomAttributeArgument(writer.Object, value) : value;
    }
}
