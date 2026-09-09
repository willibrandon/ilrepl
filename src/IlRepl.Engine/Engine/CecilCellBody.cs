using System.Reflection;
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
    /// Detects generic member references whose array dimensions Reflection.Emit would discard.
    /// </summary>
    /// <param name="state">The cell body.</param>
    /// <returns>Whether its body requires metadata emission.</returns>
    public static bool IsRequired(CellState state)
    {
        foreach (var entry in state.Entries)
        {
            switch (entry.Instruction?.Operand)
            {
                case ResolvedMethod { Method: { } method } resolved when method.IsGenericMethod
                    || method.DeclaringType is { IsGenericType: true, IsGenericTypeDefinition: false }:
                {
                    if (ContainsArray(resolved.ReturnType) || resolved.ParameterTypes.Any(ContainsArray))
                    {
                        return true;
                    }

                    break;
                }
                case FieldInfo field when field.DeclaringType is { IsGenericType: true, IsGenericTypeDefinition: false }:
                    if (ContainsArray(field.FieldType))
                    {
                        return true;
                    }

                    break;
            }
        }

        return false;
    }

    private static bool ContainsArray(Type type) => type.IsArray && !type.IsSZArray
        || type.HasElementType && ContainsArray(type.GetElementType()!)
        || type.IsGenericType && !type.IsGenericTypeDefinition && type.GetGenericArguments().Any(ContainsArray)
        || TypeNameFormatter.IsFunctionPointer(type)
            && (ContainsArray(type.GetFunctionPointerReturnType()) || type.GetFunctionPointerParameterTypes().Any(ContainsArray));

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
            run.Parameters.Add(new ParameterDefinition(argument.Name, ParameterAttributes.None, writer.Import(argument.Type)));
        }

        var map = new EmitMap(signature => methods.TryGetValue(signature.Name, out var method) ? method
            : throw new ReplException($"no method '{signature.Name}' is bound in the session"));
        CecilBodyEmitter.Emit(run, state, writer, map);
        var definition = writer.Load();
        return (definition, definition.Assembly.GetType("IlRepl.CellBody")!.GetMethod("Run")!);
    }
}
