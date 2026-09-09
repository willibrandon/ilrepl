using System.Reflection;
using Mono.Cecil;

namespace IlRepl.Engine;

/// <summary>
/// Imports loaded function-pointer signatures directly from metadata instead of Cecil's reflection importer.
/// </summary>
internal static class CecilMetadataSignatures
{
    /// <summary>
    /// Detects a method signature that Cecil cannot import through reflection.
    /// </summary>
    /// <param name="method">The loaded method.</param>
    /// <returns>Whether its signature contains a function pointer.</returns>
    public static bool IsRequired(MethodBase method) => method is MethodInfo info && ContainsPointer(info.ReturnType)
        || method.GetParameters().Any(parameter => ContainsPointer(parameter.ParameterType));

    /// <summary>
    /// Detects a field signature that Cecil cannot import through reflection.
    /// </summary>
    /// <param name="field">The loaded field.</param>
    /// <returns>Whether its signature contains a function pointer.</returns>
    public static bool IsRequired(FieldInfo field) => ContainsPointer(field.FieldType);

    private static bool ContainsPointer(Type type) => TypeNameFormatter.IsFunctionPointer(type)
        || type.HasElementType && ContainsPointer(type.GetElementType()!)
        || type.IsGenericType && !type.IsGenericTypeDefinition && type.GetGenericArguments().Any(ContainsPointer);

    /// <summary>
    /// Imports a loaded method using its definition's complete signature and the referenced construction.
    /// </summary>
    /// <param name="method">The loaded method or method instance.</param>
    /// <param name="writer">The destination assembly writer.</param>
    /// <returns>The exact method reference.</returns>
    public static MethodReference Import(MethodBase method, CecilWriter writer)
    {
        if (method is MethodInfo { IsGenericMethod: true, IsGenericMethodDefinition: false } instance)
        {
            var generic = new GenericInstanceMethod(Import(instance.GetGenericMethodDefinition(), writer));
            foreach (var argument in instance.GetGenericArguments())
            {
                generic.GenericArguments.Add(writer.Import(argument));
            }

            return generic;
        }

        var signature = RuntimeMetadataSignatures.Read(method);
        var reference = new MethodReference(method.Name, writer.Module.TypeSystem.Void, writer.Import(method.DeclaringType!))
        {
            CallingConvention = (MethodCallingConvention)signature.Convention,
            HasThis = signature.HasThis,
            ExplicitThis = signature.ExplicitThis,
        };
        for (var index = 0; index < signature.GenericParameterCount; index++)
        {
            reference.GenericParameters.Add(new GenericParameter("T" + index, reference));
        }

        reference.ReturnType = Import(signature.ReturnType, reference, writer);
        foreach (var parameter in signature.Parameters)
        {
            reference.Parameters.Add(new ParameterDefinition(Import(parameter, reference, writer)));
        }

        return reference;
    }

    /// <summary>
    /// Imports a field with its original function-pointer flags, modifiers, and optional-argument boundary.
    /// </summary>
    /// <param name="field">The loaded field.</param>
    /// <param name="writer">The destination assembly writer.</param>
    /// <returns>The exact field reference.</returns>
    public static FieldReference Import(FieldInfo field, CecilWriter writer)
    {
        var signature = RuntimeMetadataSignatures.Read(field);
        var reference = new FieldReference(field.Name, writer.Module.TypeSystem.Void, writer.Import(field.DeclaringType!));
        reference.FieldType = Import(signature, reference, writer);
        return reference;
    }

    private static TypeReference Import(IlSignature signature, MemberReference context, CecilWriter writer)
    {
        switch (signature.Kind)
        {
            case IlSignatureKind.Primitive:
            case IlSignatureKind.Named:
                return writer.Import(signature.Resolved
                    ?? throw new ReplException($"could not resolve {signature.UnresolvedName} in a member signature"));
            case IlSignatureKind.TypeParameter:
                return context.DeclaringType.GetElementType().GenericParameters[signature.Index];
            case IlSignatureKind.MethodParameter:
                return ((MethodReference)context).GenericParameters[signature.Index];
            case IlSignatureKind.ByRef:
                return new ByReferenceType(Import(signature.Element!, context, writer));
            case IlSignatureKind.Pointer:
                return new PointerType(Import(signature.Element!, context, writer));
            case IlSignatureKind.Pinned:
                return new PinnedType(Import(signature.Element!, context, writer));
            case IlSignatureKind.SzArray:
                return new ArrayType(Import(signature.Element!, context, writer));
            case IlSignatureKind.Array:
                return writer.SignatureFixups.Array(Import(signature.Element!, context, writer),
                    signature.Rank, signature.Sizes, signature.LowerBounds);
            case IlSignatureKind.GenericInstance:
            {
                var generic = new GenericInstanceType(Import(signature.Element!, context, writer));
                foreach (var argument in signature.Arguments)
                {
                    generic.GenericArguments.Add(Import(argument, context, writer));
                }

                return generic;
            }
            case IlSignatureKind.Modified:
            {
                var modifier = Import(signature.Modifier!, context, writer);
                var element = Import(signature.Element!, context, writer);
                return signature.IsRequired ? new RequiredModifierType(modifier, element) : new OptionalModifierType(modifier, element);
            }
            case IlSignatureKind.FunctionPointer:
            {
                var method = signature.Method!;
                var pointer = new FunctionPointerType
                {
                    CallingConvention = (MethodCallingConvention)method.Convention,
                    HasThis = method.HasThis,
                    ExplicitThis = method.ExplicitThis,
                    ReturnType = Import(method.ReturnType, context, writer),
                };
                for (var index = 0; index < method.Parameters.Count; index++)
                {
                    var parameter = Import(method.Parameters[index], context, writer);
                    if (index == method.RequiredParameterCount)
                    {
                        parameter = new SentinelType(parameter);
                    }

                    pointer.Parameters.Add(new ParameterDefinition(parameter));
                }

                return pointer;
            }
            default:
                throw new ReplException($"unsupported signature element {signature.Kind}");
        }
    }
}
