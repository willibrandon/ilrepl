using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace IlRepl.Engine.Binding;

/// <summary>
/// Describes runtime objects as symbols: a <see cref="Type"/>, a builder, a <see cref="MethodBase"/>,
/// a <see cref="FieldInfo"/>, or a session declaration becomes the symbol a metadata reader would
/// produce for the same definition, with the same identity. Nothing is loaded; only what reflection
/// already knows is read.
/// </summary>
public static class RuntimeSymbolImporter
{
    private static readonly ConditionalWeakTable<Type, TypeSymbol> LoadedDefinitions = [];

    /// <summary>
    /// The symbol of a runtime type.
    /// </summary>
    /// <param name="type">The type: a loaded type, a builder, a construction, or a generic parameter.</param>
    /// <returns>The symbol.</returns>
    public static TypeSymbol Import(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (type.IsGenericParameter)
        {
            // The parameter alone: its constraints may mention it, as T : IComparable<T> does, and
            // belong to the owner's declaration, not to every mention of the parameter.
            var (owner, isMethod, attributes) = ParameterFacts(type);
            return TypeSymbol.Parameter(owner, isMethod, type.GenericParameterPosition, type.Name, attributes);
        }

        if (type.IsByRef)
        {
            return TypeSymbol.ByRef(Import(type.GetElementType()!));
        }

        if (type.IsPointer)
        {
            return TypeSymbol.Pointer(Import(type.GetElementType()!));
        }

        if (type.IsArray)
        {
            var element = Import(type.GetElementType()!);
            return type.IsSZArray ? TypeSymbol.SzArray(element) : TypeSymbol.Array(element, type.GetArrayRank(), [], []);
        }

        if (TypeNameFormatter.IsFunctionPointer(type))
        {
            return TypeSymbol.FunctionPointer(ImportFunctionPointer(type));
        }

        if (CilPrimitives.KeywordOf(type) is { } keyword)
        {
            return TypeSymbol.Primitive(keyword);
        }

        if (type.IsGenericType && !type.IsGenericTypeDefinition)
        {
            return TypeSymbol.Construct(Import(type.GetGenericTypeDefinition()), [.. type.GetGenericArguments().Select(Import)]);
        }

        if (!RuntimeDefinitions.IsDynamic(type))
        {
            return LoadedDefinitions.GetValue(type, ImportDefinition);
        }

        return ImportDefinition(type);
    }

    private static TypeSymbol ImportDefinition(Type type)
    {
        var id = RuntimeDefinitions.Of(type);
        IReadOnlyList<string> parameterNames = [];
        if (type.IsGenericTypeDefinition)
        {
            var parameters = type.GetGenericArguments();
            parameterNames = parameters is null ? [] : [.. parameters.Select(p => p.Name)];
        }

        string assemblyName;
        try
        {
            assemblyName = type.Assembly.GetName().Name ?? "";
        }
        catch (NotSupportedException)
        {
            assemblyName = "";
        }

        return TypeSymbol.Named(
            id,
            TypeNameFormatter.Unescape(type.Name),
            type.Namespace ?? "",
            type.DeclaringType is { } declaring ? Import(declaring.IsGenericType && !declaring.IsGenericTypeDefinition ? declaring.GetGenericTypeDefinition() : declaring) : null,
            assemblyName,
            type.Attributes,
            type.IsValueType,
            parameterNames);
    }

    /// <summary>
    /// The symbol of a generic parameter, with its constraints.
    /// </summary>
    /// <param name="parameter">A generic parameter type.</param>
    /// <returns>The symbol.</returns>
    public static GenericParameterSymbol ImportParameter(Type parameter)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        if (!parameter.IsGenericParameter)
        {
            throw new ArgumentException($"'{parameter}' is not a generic parameter", nameof(parameter));
        }

        var (owner, isMethod, attributes) = ParameterFacts(parameter);
        var position = parameter.GenericParameterPosition;
        IReadOnlyList<TypeSymbol> constraints;
        try
        {
            constraints = parameter is GenericTypeParameterBuilder ? [] : [.. parameter.GetGenericParameterConstraints().Select(Import)];
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
        {
            constraints = [];
        }

        return new GenericParameterSymbol(owner, isMethod, position, parameter.Name, attributes, constraints);
    }

    private static (DefinitionId Owner, bool IsMethod, GenericParameterAttributes Attributes) ParameterFacts(Type parameter)
    {
        MethodBase? declaringMethod;
        try
        {
            declaringMethod = parameter.DeclaringMethod;
        }
        catch (NotSupportedException)
        {
            declaringMethod = null;
        }

        var isMethod = declaringMethod is not null;
        var owner = isMethod ? RuntimeDefinitions.Of(declaringMethod!) : RuntimeDefinitions.Of(Definition(parameter.DeclaringType!));
        RuntimeDefinitions.RememberParameter(owner, isMethod, parameter.GenericParameterPosition, parameter);
        GenericParameterAttributes attributes;
        try
        {
            attributes = parameter.GenericParameterAttributes;
        }
        catch (NotSupportedException)
        {
            attributes = GenericParameterAttributes.None;
        }

        return (owner, isMethod, attributes);
    }

    private static MethodSignatureSymbol ImportFunctionPointer(Type type)
    {
        var conventions = CallingConventions.Standard;
        var unmanaged = type.IsUnmanagedFunctionPointer;
        return new MethodSignatureSymbol(
            conventions,
            unmanaged,
            System.Runtime.InteropServices.CallingConvention.Winapi,
            Import(type.GetFunctionPointerReturnType()),
            [.. type.GetFunctionPointerParameterTypes().Select(Import)],
            null);
    }

    /// <summary>
    /// The symbol of a loaded method or constructor, on the type reflection declares it on.
    /// </summary>
    /// <param name="method">The method.</param>
    /// <param name="declaring">The declaring type as a symbol, or null to import it from the method.</param>
    /// <returns>The symbol.</returns>
    public static MethodSymbol Import(MethodBase method, TypeSymbol? declaring = null)
    {
        ArgumentNullException.ThrowIfNull(method);
        declaring ??= method.DeclaringType is { } declaringType ? Import(declaringType) : null;
        var id = RuntimeDefinitions.Of(method);
        var info = method as MethodInfo;
        var parameters = method.GetParameters();
        IReadOnlyList<GenericParameterSymbol> genericParameters = [];
        IReadOnlyList<TypeSymbol> genericArguments = [];
        if (info is { IsGenericMethod: true })
        {
            var definition = info.IsGenericMethodDefinition ? info : info.GetGenericMethodDefinition();
            genericParameters = [.. definition.GetGenericArguments().Select(ImportParameter)];
            if (!info.IsGenericMethodDefinition)
            {
                genericArguments = [.. info.GetGenericArguments().Select(Import)];
            }
        }

        MethodImplAttributes impl;
        try
        {
            impl = method.GetMethodImplementationFlags();
        }
        catch (NotSupportedException)
        {
            impl = MethodImplAttributes.IL;
        }

        return new MethodSymbol
        {
            Definition = id,
            Source = MethodSymbolSource.Loaded,
            DeclaringType = declaring,
            Name = method is ConstructorInfo ? (method.IsStatic ? ".cctor" : ".ctor") : method.Name,
            Attributes = method.Attributes,
            ImplAttributes = impl,
            CallingConvention = method.CallingConvention,
            ReturnType = info is null ? TypeSymbol.Void : Import(info.ReturnType),
            Parameters = [.. parameters.Select(ImportParameter)],
            GenericParameters = genericParameters,
            GenericArguments = genericArguments,
            ReturnRequiredModifiers = info is null ? [] : Modifiers(() => info.ReturnParameter.GetRequiredCustomModifiers()),
            ReturnOptionalModifiers = info is null ? [] : Modifiers(() => info.ReturnParameter.GetOptionalCustomModifiers()),
        };
    }

    private static ParameterSymbol ImportParameter(ParameterInfo parameter) => new(Import(parameter.ParameterType), parameter.Name)
    {
        RequiredModifiers = Modifiers(parameter.GetRequiredCustomModifiers),
        OptionalModifiers = Modifiers(parameter.GetOptionalCustomModifiers),
    };

    private static IReadOnlyList<TypeSymbol> Modifiers(Func<Type[]> read)
    {
        try
        {
            return [.. read().Select(Import)];
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or NotImplementedException)
        {
            // A builder, or a member seen through a builder's instantiation, cannot list its modifiers.
            return [];
        }
    }

    /// <summary>
    /// The symbol of a loaded field, on the type reflection declares it on.
    /// </summary>
    /// <param name="field">The field.</param>
    /// <param name="declaring">The declaring type as a symbol, or null to import it from the field.</param>
    /// <returns>The symbol.</returns>
    public static FieldSymbol Import(FieldInfo field, TypeSymbol? declaring = null)
    {
        ArgumentNullException.ThrowIfNull(field);
        declaring ??= Import(field.DeclaringType!);
        return new FieldSymbol
        {
            Definition = RuntimeDefinitions.Of(field),
            Source = MethodSymbolSource.Loaded,
            DeclaringType = declaring,
            Name = field.Name,
            FieldType = Import(field.FieldType),
            Attributes = field.Attributes,
            RequiredModifiers = Modifiers(field.GetRequiredCustomModifiers),
            OptionalModifiers = Modifiers(field.GetOptionalCustomModifiers),
        };
    }

    /// <summary>
    /// The symbol of a declared signature: a session method, or a member of a type being written.
    /// </summary>
    /// <param name="signature">The signature as declared.</param>
    /// <param name="declaring">The declaring type, or null for a session method.</param>
    /// <param name="id">The member's identity.</param>
    /// <param name="source">Where the member comes from.</param>
    /// <param name="declared">True when the member's header has been seen.</param>
    /// <returns>The symbol.</returns>
    public static MethodSymbol Import(MethodSignature signature, TypeSymbol? declaring, DefinitionId id, MethodSymbolSource source, bool declared)
    {
        ArgumentNullException.ThrowIfNull(signature);
        var genericParameters = new List<GenericParameterSymbol>();
        for (var i = 0; i < signature.TypeParameters.Count; i++)
        {
            var declaration = signature.TypeParameters[i];
            genericParameters.Add(new GenericParameterSymbol(id, true, i, declaration.Name, declaration.Attributes, [.. declaration.Constraints.Select(Import)]));
        }

        return new MethodSymbol
        {
            Definition = id,
            Source = source,
            DeclaringType = declaring,
            Name = signature.Name,
            Attributes = signature.Attributes,
            ImplAttributes = signature.ImplAttributes,
            CallingConvention = signature.CallingConvention,
            ReturnType = Import(signature.ReturnType),
            Parameters = [.. signature.Parameters.Select(p => new ParameterSymbol(Import(p.Type), p.Name) { RequiredModifiers = [.. p.RequiredModifiers.Select(Import)], OptionalModifiers = [.. p.OptionalModifiers.Select(Import)] })],
            GenericParameters = genericParameters,
            ReturnRequiredModifiers = [.. signature.ReturnRequiredModifiers.Select(Import)],
            ReturnOptionalModifiers = [.. signature.ReturnOptionalModifiers.Select(Import)],
            IsDeclared = declared,
        };
    }

    /// <summary>
    /// The symbol of a declared field of a type being written.
    /// </summary>
    /// <param name="declaration">The declaration.</param>
    /// <param name="declaring">The declaring type.</param>
    /// <param name="id">The field's identity.</param>
    /// <returns>The symbol.</returns>
    public static FieldSymbol Import(FieldDeclaration declaration, TypeSymbol declaring, DefinitionId id)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        ArgumentNullException.ThrowIfNull(declaring);
        return new FieldSymbol
        {
            Definition = id,
            Source = MethodSymbolSource.Declared,
            DeclaringType = declaring,
            Name = declaration.Name,
            FieldType = Import(declaration.Type),
            Attributes = declaration.Attributes,
            RequiredModifiers = [.. declaration.RequiredModifiers.Select(Import)],
            OptionalModifiers = [.. declaration.OptionalModifiers.Select(Import)],
        };
    }

    private static Type Definition(Type type) => type.IsGenericType && !type.IsGenericTypeDefinition ? type.GetGenericTypeDefinition() : type;
}
