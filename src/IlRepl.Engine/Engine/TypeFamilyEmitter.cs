using System.Reflection;
using System.Reflection.Emit;
using IlRepl.Engine.Binding;
using Mono.Cecil;
using CecilEventAttributes = Mono.Cecil.EventAttributes;
using CecilFieldAttributes = Mono.Cecil.FieldAttributes;
using CecilGenericParameterAttributes = Mono.Cecil.GenericParameterAttributes;
using CecilMethodAttributes = Mono.Cecil.MethodAttributes;
using CecilMethodImplAttributes = Mono.Cecil.MethodImplAttributes;
using CecilParameterAttributes = Mono.Cecil.ParameterAttributes;
using CecilPropertyAttributes = Mono.Cecil.PropertyAttributes;
using CecilTypeAttributes = Mono.Cecil.TypeAttributes;
using NamedArgument = Mono.Cecil.CustomAttributeNamedArgument;

namespace IlRepl.Engine;

/// <summary>
/// Writes one family through the shared declaration, shape, member, detail and body phases.
/// </summary>
internal sealed class TypeFamilyEmitter(
    CecilWriter writer,
    IReadOnlyDictionary<string, (TypeBuilder Prototype, OwnMembers Members)> prototypes,
    IReadOnlyDictionary<string, MethodTrampoline> trampolines)
{
    private readonly Dictionary<string, TypeDefinition> _definitions = new(StringComparer.Ordinal);
    private readonly Dictionary<MethodDeclaration, MethodDefinition> _methods = new(ReferenceEqualityComparer.Instance);
    private readonly EmitMap _map = new(signature => trampolines.TryGetValue(signature.Name, out var trampoline)
        ? trampoline.Method : throw new ReplException($"no method '{signature.Name}' is bound in the session"));

    /// <summary>
    /// The loaded types of the family by path, for an export: their members map onto the definitions too.
    /// </summary>
    public IReadOnlyDictionary<string, Type>? RuntimeTypes { get; init; }

    private Type? RuntimeTypeOf(string path) => RuntimeTypes is not null && RuntimeTypes.TryGetValue(path, out var type) ? type : null;

    /// <summary>
    /// Declares all type identities before their shapes are imported.
    /// </summary>
    /// <param name="family">The outer declaration and its nested types.</param>
    public void Declare(TypeDeclaration family) => DefineTypes(family, null);

    /// <summary>
    /// Defines bases, interfaces, layout and generic parameters for the family.
    /// </summary>
    /// <param name="family">The outer declaration and its nested types.</param>
    public void Shape(TypeDeclaration family)
    {
        foreach (var declaration in family.Family)
        {
            DefineShape(declaration);
        }
    }

    /// <summary>
    /// Declares member signatures before references to them are emitted.
    /// </summary>
    /// <param name="family">The outer declaration and its nested types.</param>
    public void Members(TypeDeclaration family)
    {
        foreach (var declaration in family.Family)
        {
            DefineMembers(declaration);
        }
    }

    /// <summary>
    /// Writes attributes, constants and overrides after member identities exist.
    /// </summary>
    /// <param name="family">The outer declaration and its nested types.</param>
    public void Details(TypeDeclaration family)
    {
        foreach (var declaration in family.Family)
        {
            DefineDetails(declaration);
        }
    }

    /// <summary>
    /// Emits every concrete method body against the completed family mappings.
    /// </summary>
    /// <param name="family">The outer declaration and its nested types.</param>
    public void Bodies(TypeDeclaration family)
    {
        foreach (var declaration in family.Family)
        {
            EmitBodies(declaration);
        }
    }

    private void DefineTypes(TypeDeclaration declaration, TypeDefinition? enclosing)
    {
        var (prototype, _) = prototypes[declaration.FullName];
        var attributes = (CecilTypeAttributes)declaration.Attributes;
        TypeDefinition definition;
        if (enclosing is null)
        {
            definition = writer.DefineType(declaration.Namespace, declaration.Name, attributes, null);
        }
        else
        {
            definition = new TypeDefinition("", declaration.Name, attributes, null);
            enclosing.NestedTypes.Add(definition);
        }

        _definitions[declaration.FullName] = definition;
        writer.Define(prototype, definition);
        var runtime = RuntimeTypeOf(declaration.FullName);
        if (runtime is not null)
        {
            writer.Define(runtime, definition);
        }

        var builders = prototype.IsGenericTypeDefinition ? prototype.GetGenericArguments() : [];
        var runtimeParameters = runtime is { IsGenericTypeDefinition: true } ? runtime.GetGenericArguments() : [];
        for (var i = 0; i < declaration.TypeParameters.Count; i++)
        {
            var parameter = new GenericParameter(declaration.TypeParameters[i].Name, definition);
            definition.GenericParameters.Add(parameter);
            if (i < builders.Length)
            {
                writer.Define(builders[i], parameter);
            }

            if (i < runtimeParameters.Length)
            {
                writer.Define(runtimeParameters[i], parameter);
            }
        }

        foreach (var nested in declaration.NestedTypes)
        {
            DefineTypes(nested, definition);
        }
    }

    private void DefineShape(TypeDeclaration declaration)
    {
        var definition = _definitions[declaration.FullName];
        if (declaration.BaseType is not null)
        {
            definition.BaseType = writer.Import(declaration.BaseType);
        }

        foreach (var iface in declaration.Interfaces)
        {
            definition.Interfaces.Add(new InterfaceImplementation(writer.Import(iface)));
        }

        for (var i = 0; i < declaration.TypeParameters.Count; i++)
        {
            var declared = declaration.TypeParameters[i];
            var parameter = definition.GenericParameters[i];
            parameter.Attributes = (CecilGenericParameterAttributes)declared.Attributes;
            foreach (var constraint in declared.Constraints)
            {
                parameter.Constraints.Add(new GenericParameterConstraint(writer.Import(constraint)));
            }
        }

        if (declaration.PackingSize is not null || declaration.ClassSize is not null)
        {
            // Both fields are written; an unset one would be -1 in the row.
            definition.PackingSize = (short)(declaration.PackingSize ?? 0);
            definition.ClassSize = declaration.ClassSize ?? 0;
        }

    }

    private void DefineMembers(TypeDeclaration declaration)
    {
        var definition = _definitions[declaration.FullName];
        var (prototype, members) = prototypes[declaration.FullName];
        foreach (var field in declaration.Fields)
        {
            var fieldType = field.ExactType is null ? writer.Import(field.Type) : writer.Import(field.ExactType);
            var type = Modified(fieldType, field.RequiredModifiers, field.OptionalModifiers);
            var cecilField = new FieldDefinition(field.Name, (CecilFieldAttributes)field.Attributes, type);
            if (field.Offset is { } offset)
            {
                cecilField.Offset = offset;
            }

            if (field.HasDefault)
            {
                cecilField.Constant = ConstantFor(field.DefaultValue);
            }

            definition.Fields.Add(cecilField);
            var builder = members.FindField(field.Name)?.Builder;
            if (builder is not null)
            {
                writer.Define(builder, cecilField);
            }

            if (RuntimeTypeOf(declaration.FullName)?.GetField(field.Name, AllMembers) is { } runtimeField)
            {
                writer.Define(runtimeField, cecilField);
            }
        }

        foreach (var method in declaration.Methods)
        {
            var signature = method.Signature;
            var builder = FindBuilder(members, signature);
            var cecilMethod = new MethodDefinition(signature.Name, (CecilMethodAttributes)signature.Attributes, writer.Module
                .TypeSystem.Void)
            {
                ImplAttributes = (CecilMethodImplAttributes)signature.ImplAttributes,
                HasThis = !signature.IsStatic,
                ExplicitThis = signature.CallingConvention.HasFlag(CallingConventions.ExplicitThis),
            };
            if (signature.CallingConvention.HasFlag(CallingConventions.VarArgs))
            {
                cecilMethod.CallingConvention = MethodCallingConvention.VarArg;
            }

            definition.Methods.Add(cecilMethod);
            _methods[method] = cecilMethod;
            if (RuntimeMethodOf(declaration, method) is { } runtimeMethod)
            {
                writer.Define(runtimeMethod, cecilMethod);
                var runtimeGenerics = runtimeMethod is MethodInfo { IsGenericMethodDefinition: true } generic ? generic
                    .GetGenericArguments() : [];
                for (var i = 0; i < runtimeGenerics.Length && i < signature.TypeParameters.Count; i++)
                {
                    // Registered below once the definition's own parameters exist.
                    _runtimeMethodParameters[(cecilMethod, i)] = runtimeGenerics[i];
                }
            }

            if (builder is not null)
            {
                writer.Define(builder, cecilMethod);
                var methodBuilders = builder is MethodBuilder mb && mb.IsGenericMethodDefinition ? mb.GetGenericArguments() : [];
                for (var i = 0; i < signature.TypeParameters.Count; i++)
                {
                    var parameter = new GenericParameter(signature.TypeParameters[i].Name, cecilMethod);
                    cecilMethod.GenericParameters.Add(parameter);
                    if (i < methodBuilders.Length)
                    {
                        writer.Define(methodBuilders[i], parameter);
                    }

                    if (_runtimeMethodParameters.TryGetValue((cecilMethod, i), out var runtimeParameter))
                    {
                        writer.Define(runtimeParameter, parameter);
                    }
                }
            }
        }

        _ = prototype;
    }

    /// <summary>
    /// Imports signatures, attributes, properties and events after all referenced type and member identities exist.
    /// </summary>
    private void DefineDetails(TypeDeclaration declaration)
    {
        var definition = _definitions[declaration.FullName];
        foreach (var attribute in declaration.CustomAttributes)
        {
            definition.CustomAttributes.Add(Attribute(attribute));
        }

        foreach (var field in declaration.Fields)
        {
            var cecilField = definition.Fields.First(f => f.Name == field.Name);
            foreach (var attribute in field.CustomAttributes)
            {
                cecilField.CustomAttributes.Add(Attribute(attribute));
            }
        }

        foreach (var method in declaration.Methods)
        {
            var signature = method.Signature;
            var cecilMethod = _methods[method];
            var exact = signature.ExactSymbol;
            var returnType = exact is not null
                ? writer.Import(exact.ReturnType)
                : writer.Import(signature.ReturnType);
            cecilMethod.ReturnType = Modified(returnType, signature.ReturnRequiredModifiers, signature.ReturnOptionalModifiers);
            for (var i = 0; i < signature.TypeParameters.Count; i++)
            {
                var declared = signature.TypeParameters[i];
                var parameter = cecilMethod.GenericParameters[i];
                parameter.Attributes = (CecilGenericParameterAttributes)declared.Attributes;
                foreach (var constraint in declared.Constraints)
                {
                    parameter.Constraints.Add(new GenericParameterConstraint(writer.Import(constraint)));
                }
            }

            for (var i = 0; i < signature.Parameters.Count; i++)
            {
                var parameter = signature.Parameters[i];
                var exactType = exact is null ? writer.Import(parameter.Type) : writer.Import(exact.Parameters[i].Type);
                var type = Modified(exactType, parameter.RequiredModifiers, parameter.OptionalModifiers);
                var cecilParameter = new ParameterDefinition(parameter.Name ?? ("arg" + i.ToString(System.Globalization
                    .CultureInfo.InvariantCulture)), (CecilParameterAttributes)parameter.Attributes, type);
                if (parameter.HasDefault)
                {
                    cecilParameter.Constant = ConstantFor(parameter.DefaultValue);
                }

                foreach (var attribute in parameter.CustomAttributes)
                {
                    cecilParameter.CustomAttributes.Add(Attribute(attribute));
                }

                cecilMethod.Parameters.Add(cecilParameter);
            }

            foreach (var attribute in signature.CustomAttributes)
            {
                cecilMethod.CustomAttributes.Add(Attribute(attribute));
            }

            foreach (var attribute in signature.ReturnCustomAttributes)
            {
                cecilMethod.MethodReturnType.CustomAttributes.Add(Attribute(attribute));
            }
        }

        foreach (var property in declaration.Properties)
        {
            var cecilProperty = new PropertyDefinition(property.Name, (CecilPropertyAttributes)property.Attributes, writer
                .Import(property.Type))
            {
                HasThis = !property.IsStatic,
            };
            foreach (var parameterType in property.ParameterTypes)
            {
                cecilProperty.Parameters.Add(new ParameterDefinition(writer.Import(parameterType)));
            }

            if (property.HasDefault)
            {
                cecilProperty.Constant = ConstantFor(property.DefaultValue);
            }

            cecilProperty.GetMethod = property.Getter is null ? null : _methods[property.Getter];
            cecilProperty.SetMethod = property.Setter is null ? null : _methods[property.Setter];
            foreach (var other in property.Others)
            {
                cecilProperty.OtherMethods.Add(_methods[other]);
            }

            foreach (var attribute in property.CustomAttributes)
            {
                cecilProperty.CustomAttributes.Add(Attribute(attribute));
            }

            definition.Properties.Add(cecilProperty);
        }

        foreach (var evt in declaration.Events)
        {
            var cecilEvent = new EventDefinition(evt.Name, (CecilEventAttributes)evt.Attributes, writer.Import(evt.HandlerType))
            {
                AddMethod = _methods[evt.AddOn],
                RemoveMethod = _methods[evt.RemoveOn],
                InvokeMethod = evt.Fire is null ? null : _methods[evt.Fire],
            };
            foreach (var attribute in evt.CustomAttributes)
            {
                cecilEvent.CustomAttributes.Add(Attribute(attribute));
            }

            definition.Events.Add(cecilEvent);
        }
    }

    private void EmitBodies(TypeDeclaration declaration)
    {
        foreach (var method in declaration.Methods)
        {
            var cecilMethod = _methods[method];
            foreach (var over in method.Overrides)
            {
                cecilMethod.Overrides.Add(writer.Import(over.Target, over.Target.DeclaringType));
            }

            if (method.Body is { } body)
            {
                CecilBodyEmitter.Emit(cecilMethod, body, writer, _map);
            }
        }

        foreach (var over in declaration.Overrides)
        {
            var implementing = declaration.Methods.FirstOrDefault(m => m.Name == over.BodyName && m.IsStatic == over.BodyIsStatic
                && TypeIdentity.Equal(m.Signature.ReturnType, over.BodyReturnType)
                && m.Signature.Parameters.Count == over.BodyParameterTypes.Count
                && m.Signature.ParameterTypes.Zip(over.BodyParameterTypes).All(p => TypeIdentity.Equal(p.First, p.Second)))
                ?? throw new ReplException($".override names {over.BodyName}, which {declaration.FullName} does not declare");
            _methods[implementing].Overrides.Add(writer.Import(over.Target, over.Target.DeclaringType));
        }
    }

    private const BindingFlags AllMembers = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags
        .Instance | BindingFlags.DeclaredOnly;
    private readonly Dictionary<(MethodDefinition, int), Type> _runtimeMethodParameters = [];

    /// <summary>
    /// The loaded method behind a declaration, matched by name, kind, and parameter shape.
    /// </summary>
    private MethodBase? RuntimeMethodOf(TypeDeclaration declaration, MethodDeclaration method)
    {
        var runtime = RuntimeTypeOf(declaration.FullName);
        if (runtime is null)
        {
            return null;
        }

        var signature = method.Signature;
        var exact = signature.ExactSymbol;
        var wanted = signature.ParameterTypes.Select(TypeNameFormatter.Pretty).ToArray();
        IEnumerable<MethodBase> candidates = method.IsConstructor || method.IsTypeInitializer
            ? runtime.GetConstructors(AllMembers).Where(c => c.IsStatic == method.IsTypeInitializer)
            : runtime.GetMethods(AllMembers).Where(m => m.Name == signature.Name && m.IsStatic == signature.IsStatic);
        return candidates.FirstOrDefault(m =>
        {
            if (exact is not null)
            {
                return SignatureSymbolIdentity.Equal(exact, RuntimeSymbolImporter.Import(m));
            }

            var parameters = m.GetParameters();
            return parameters.Length == wanted.Length && parameters.Select(p => TypeNameFormatter.Pretty(p.ParameterType))
                .SequenceEqual(wanted);
        });
    }

    private static MethodBase? FindBuilder(OwnMembers members, MethodSignature signature)
    {
        foreach (var (declared, builder, isDeclared) in members.Methods)
        {
            if (isDeclared && ReferenceEquals(declared, signature))
            {
                return builder;
            }
        }

        foreach (var (declared, builder, isDeclared) in members.Methods)
        {
            if (isDeclared && declared.Name == signature.Name && declared.IsStatic == signature.IsStatic && declared
                .Parameters.Count == signature.Parameters.Count
                && SignatureIdentity.Same(declared, signature))
            {
                return builder;
            }
        }

        return null;
    }

    private TypeReference Modified(TypeReference type, IReadOnlyList<Type> required, IReadOnlyList<Type> optional)
    {
        var result = type;
        foreach (var modifier in optional)
        {
            result = new OptionalModifierType(writer.Import(modifier), result);
        }

        foreach (var modifier in required)
        {
            result = new RequiredModifierType(writer.Import(modifier), result);
        }

        return result;
    }

    private static object? ConstantFor(object? value) => value is Enum e ? System.Convert.ChangeType(e, Enum
        .GetUnderlyingType(e.GetType()), System.Globalization.CultureInfo.InvariantCulture) : value;

    private CustomAttribute Attribute(CustomAttributeDeclaration declaration)
    {
        var attribute = new CustomAttribute(writer.Import(declaration.Constructor));
        var parameters = declaration.Constructor.GetParameters();
        for (var i = 0; i < declaration.FixedArguments.Count; i++)
        {
            attribute.ConstructorArguments.Add(Argument(parameters[i].ParameterType, declaration.FixedArguments[i]));
        }

        foreach (var (field, value) in declaration.NamedFields)
        {
            attribute.Fields.Add(new NamedArgument(field.Name, Argument(field.FieldType, value)));
        }

        foreach (var (property, value) in declaration.NamedProperties)
        {
            attribute.Properties.Add(new NamedArgument(property.Name, Argument(property.PropertyType, value)));
        }

        return attribute;
    }

    private CustomAttributeArgument Argument(Type declared, object? value)
    {
        if (declared == typeof(object))
        {
            // A boxed value carries its own type in the blob.
            var actual = value switch
            {
                null => typeof(object),
                Type => typeof(Type),
                _ => value.GetType(),
            };
            return new CustomAttributeArgument(writer.Import(typeof(object)), value is null ? null : Argument(actual, value));
        }

        if (declared == typeof(Type))
        {
            return new CustomAttributeArgument(writer.Import(typeof(Type)), value is Type t ? writer.Import(t) : null);
        }

        if (declared.IsArray)
        {
            if (value is not Array array)
            {
                return new CustomAttributeArgument(writer.Import(declared), null);
            }

            var element = declared.GetElementType()!;
            var items = new CustomAttributeArgument[array.Length];
            for (var i = 0; i < array.Length; i++)
            {
                items[i] = Argument(element, array.GetValue(i));
            }

            return new CustomAttributeArgument(writer.Import(declared), items);
        }

        if (declared.IsEnum)
        {
            return new CustomAttributeArgument(writer.Import(declared), ConstantFor(value));
        }

        return new CustomAttributeArgument(writer.Import(declared), value);
    }
}
