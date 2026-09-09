using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace IlRepl.Engine.Binding;

/// <summary>
/// Restores signature details that reflection's runtime types cannot retain.
/// </summary>
internal static class RuntimeSignatureShapes
{
    /// <summary>
    /// Adds metadata bounds and calling conventions to an already resolved method signature.
    /// </summary>
    /// <param name="method">The reflected method.</param>
    /// <param name="symbol">The imported signature.</param>
    /// <returns>The signature with its original bounds and function-pointer flags.</returns>
    public static MethodSymbol Restore(MethodBase method, MethodSymbol symbol)
    {
        if (!NeedsMetadata(symbol.ReturnType) && !symbol.Parameters.Any(parameter => NeedsMetadata(parameter.Type)))
        {
            return symbol;
        }

        return Read(method, symbol, (source, provider) =>
        {
            var handle = (MethodDefinitionHandle)MetadataTokens.Handle(symbol.Definition.Token);
            var signature = source.Reader.GetMethodDefinition(handle).DecodeSignature(provider, SymbolGenericOwner.None);
            return symbol.With(symbol.DeclaringType, RestoreRoot(symbol.ReturnType, signature.ReturnType),
                [.. symbol.Parameters.Select((parameter, index) => parameter with
                {
                    Type = RestoreRoot(parameter.Type, signature.ParameterTypes[index]),
                })], symbol.GenericArguments);
        });
    }

    /// <summary>
    /// Adds metadata bounds and calling conventions to a field's already resolved type.
    /// </summary>
    /// <param name="field">The reflected field.</param>
    /// <param name="symbol">The imported field.</param>
    /// <returns>The field with its original bounds and function-pointer flags.</returns>
    public static FieldSymbol Restore(FieldInfo field, FieldSymbol symbol) => !NeedsMetadata(symbol.FieldType) ? symbol
        : Read(field, symbol, (source, provider) =>
        {
            var handle = (FieldDefinitionHandle)MetadataTokens.Handle(symbol.Definition.Token);
            var type = source.Reader.GetFieldDefinition(handle).DecodeSignature(provider, SymbolGenericOwner.None);
            return symbol.With(symbol.DeclaringType, RestoreRoot(symbol.FieldType, type));
        });

    private static T Read<T>(MemberInfo member, T fallback, Func<AssemblySymbolSource, SymbolSignatureProvider, T> read)
    {
        try
        {
            if (AssemblySymbolSource.For(member.Module.Assembly) is not { } source)
            {
                return fallback;
            }

            using var lease = source.Lease();
            var provider = new SymbolSignatureProvider(source, new LoadedBindingCatalog([]),
                (handle, _) => RuntimeSymbolImporter.Import(member.Module.ResolveType(MetadataTokens.GetToken(handle))));
            return read(source, provider);
        }
        catch (Exception exception) when (ReplRecovery.IsRecoverable(exception))
        {
            // Dynamic members and unavailable metadata expose only the runtime type.
            return fallback;
        }
    }

    private static bool NeedsMetadata(TypeSymbol type) => type.Element is not null
        || type.Kind == TypeSymbolKind.FunctionPointer;

    private static TypeSymbol RestoreRoot(TypeSymbol actual, TypeSymbol metadata) =>
        Restore(actual, SymbolSignatureProvider.StripModifiers(metadata, out _, out _));

    private static TypeSymbol Restore(TypeSymbol actual, TypeSymbol metadata)
    {
        if (metadata.Kind == TypeSymbolKind.Modified)
        {
            return TypeSymbol.Modified(Restore(actual.Unwrapped, metadata.Element!), metadata.Modifier!, metadata.IsRequired);
        }

        if (actual.Kind != metadata.Kind)
        {
            // Substitution can turn a metadata generic parameter into any runtime type.
            return actual;
        }

        return actual.Kind switch
        {
            TypeSymbolKind.Array => TypeSymbol.Array(Restore(actual.Element!, metadata.Element!),
                metadata.Rank, metadata.Sizes, metadata.LowerBounds),
            TypeSymbolKind.SzArray => TypeSymbol.SzArray(Restore(actual.Element!, metadata.Element!)),
            TypeSymbolKind.ByRef => TypeSymbol.ByRef(Restore(actual.Element!, metadata.Element!)),
            TypeSymbolKind.Pointer => TypeSymbol.Pointer(Restore(actual.Element!, metadata.Element!)),
            TypeSymbolKind.Constructed => TypeSymbol.Construct(actual.Element!,
                [.. actual.Arguments.Select((argument, index) => Restore(argument, metadata.Arguments[index]))]),
            TypeSymbolKind.FunctionPointer => TypeSymbol.FunctionPointer(actual.Signature! with
            {
                ManagedConvention = metadata.Signature!.ManagedConvention,
                SentinelIndex = metadata.Signature.SentinelIndex,
                ReturnType = Restore(actual.Signature.ReturnType, metadata.Signature!.ReturnType),
                Parameters = [.. actual.Signature.Parameters.Select((parameter, index)
                    => Restore(parameter, metadata.Signature.Parameters[index]))],
            }),
            _ => actual,
        };
    }
}
