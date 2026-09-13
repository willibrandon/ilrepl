using System.Reflection;
using IlRepl.Engine.Binding;
using Mono.Cecil;
using MethodAttributes = Mono.Cecil.MethodAttributes;
using ParameterAttributes = Mono.Cecil.ParameterAttributes;
using TypeAttributes = Mono.Cecil.TypeAttributes;

namespace IlRepl.Engine;

/// <summary>
/// Writes cell bodies whose member references require signature information unavailable to Reflection.Emit.
/// </summary>
internal static class CecilCellBody
{
    /// <summary>
    /// Detects body metadata shapes that Reflection.Emit would discard from runtime projections.
    /// </summary>
    /// <param name="state">The cell body.</param>
    /// <returns>Whether its body requires metadata emission.</returns>
    public static bool IsRequired(CellState state)
    {
        if (state.Locals.Any(local => local.ExactType is not null)
            || state.Arguments.Any(argument => argument.ExactType is not null))
        {
            return true;
        }

        foreach (var entry in state.Entries)
        {
            if (entry.Instruction?.ExactTypeOperand is not null
                || entry.Instruction?.ExactFieldDeclaringType is not null
                || entry.Instruction?.Operand is CalliSignature { ExactSymbol: { } exact }
                    && RuntimeSymbolTypes.RequiresExact(exact)
                || entry.Instruction?.Operand is ResolvedMethod { ExactGenericArguments: not null }
                || entry.Instruction?.Operand is ResolvedMethod { ExactDeclaringType: not null }
                || entry.Instruction?.Operand is ResolvedMethod { ExactOptionalParameterTypes: { } optional }
                    && optional.Any(RuntimeSymbolTypes.RequiresExact)
                || entry.Instruction?.Operand is ResolvedMethod methodOperand && RequiresMetadata(methodOperand)
                || entry.Instruction?.Operand is FieldInfo fieldOperand && RequiresMetadata(fieldOperand))
            {
                return true;
            }

            switch (entry.Instruction?.Operand)
            {
                case ResolvedMethod { Method: { } method } resolved when method.IsGenericMethod
                    || method.DeclaringType is { IsGenericType: true, IsGenericTypeDefinition: false }:
                {
                    if (NeedsMetadata(resolved.ReturnType) || resolved.ParameterTypes.Any(NeedsMetadata))
                    {
                        return true;
                    }

                    break;
                }
                case FieldInfo field when field.DeclaringType is { IsGenericType: true, IsGenericTypeDefinition: false }:
                    if (NeedsMetadata(field.FieldType))
                    {
                        return true;
                    }

                    break;
            }
        }

        return false;
    }

    private static bool NeedsMetadata(Type type) => TypeNameFormatter.IsFunctionPointer(type)
        || type.HasElementType || type.IsConstructedGenericType;

    private static bool RequiresMetadata(ResolvedMethod method)
    {
        var signature = method.Definition ?? method.Declared;
        if (signature?.ExactReturnType is not null || signature?.Parameters.Any(parameter => parameter.ExactType is not null) == true)
        {
            return true;
        }

        return method.Method is { } runtime && !runtime.Module.Assembly.IsDynamic
            && CecilMetadataSignatures.IsRequired(runtime);
    }

    private static bool RequiresMetadata(FieldInfo field) => RuntimeFieldSignatures.TypeOf(field) is { } exact
        ? RuntimeSymbolTypes.RequiresExact(exact)
        : !field.Module.Assembly.IsDynamic && CecilMetadataSignatures.IsRequired(field);

    /// <summary>
    /// Emits a callable cell body with exact metadata references and the cell's existing session bindings.
    /// </summary>
    /// <param name="state">The validated cell body.</param>
    /// <param name="names">The cell's generic parameter names.</param>
    /// <param name="methods">The current session method entry points.</param>
    /// <returns>The owned body assembly and its entry point.</returns>
    public static (DefinitionAssembly Definition, MethodInfo Method) Compile(
        CellState state, IReadOnlyList<string> names, IReadOnlyDictionary<string, MethodInfo> methods)
    {
        var writer = new CecilWriter(SessionAssemblyKind.Cell);
        var type = writer.DefineType("IlRepl", "CellBody",
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit, writer.Object);
        var run = new MethodDefinition("Run", MethodAttributes.Public | MethodAttributes.Static, writer.Object);
        type.Methods.Add(run);
        for (var index = 0; index < names.Count; index++)
        {
            var parameter = new GenericParameter(names[index], run);
            run.GenericParameters.Add(parameter);
            writer.Define(state.Generics.MethodArguments[index], parameter);
        }

        if (state.IsVarArg)
        {
            run.CallingConvention = MethodCallingConvention.VarArg;
        }

        foreach (var argument in state.Arguments)
        {
            var argumentType = argument.ExactType is null ? writer.Import(argument.Type) : writer.Import(argument.ExactType);
            run.Parameters.Add(new ParameterDefinition(argument.Name, ParameterAttributes.None, argumentType));
        }

        var map = new EmitMap(signature => methods.TryGetValue(signature.Name, out var method) ? method
            : throw new ReplException($"no method '{signature.Name}' is bound in the session"));
        CecilBodyEmitter.Emit(run, state, writer, map);
        var definition = writer.Load();
        return (definition, definition.Assembly.GetType("IlRepl.CellBody")!.GetMethod("Run")!);
    }
}
