using System.Globalization;
using System.Reflection;
using Mono.Cecil;
using MethodAttributes = Mono.Cecil.MethodAttributes;
using ParameterAttributes = Mono.Cecil.ParameterAttributes;
using TypeAttributes = Mono.Cecil.TypeAttributes;

namespace IlRepl.Engine;

/// <summary>
/// Writes and loads method definitions, optionally preparing their bodies for execution.
/// </summary>
public static class DefinitionCompiler
{
    /// <summary>
    /// Compiles one version of a session method.
    /// </summary>
    /// <param name="signature">The method's signature.</param>
    /// <param name="state">The validated body.</param>
    /// <param name="trampoline">The trampoline the version will be bound into.</param>
    /// <param name="trampolines">The trampolines of every session method the body may call, by name, including this one.</param>
    /// <param name="prepare">True to ask the JIT to compile the body.</param>
    /// <returns>The version, not yet bound.</returns>
    /// <exception cref="ReplException">The emitter, the loader, or the JIT rejected the body.</exception>
    public static CompiledMethodVersion CompileMethod(
        MethodSignature signature,
        CellState state,
        MethodTrampoline trampoline,
        IReadOnlyDictionary<string, MethodTrampoline> trampolines,
        bool prepare) =>
        CompileMethod(signature, state, trampoline, trampolines, prepare, null);

    /// <summary>
    /// Compiles a method version with references to prototypes of families emitted in the same group.
    /// </summary>
    /// <param name="signature">The method's signature.</param>
    /// <param name="state">The validated body.</param>
    /// <param name="trampoline">The trampoline the version will be bound into.</param>
    /// <param name="trampolines">The trampolines of every session method the body may call, by name, including this one.</param>
    /// <param name="prepare">True to ask the JIT to compile the body.</param>
    /// <param name="externals">Prototypes of families in the group, referenced by their assembly names, or null.</param>
    /// <returns>The version, not yet bound.</returns>
    /// <exception cref="ReplException">The emitter, the loader, or the JIT rejected the body.</exception>
    public static CompiledMethodVersion CompileMethod(
        MethodSignature signature,
        CellState state,
        MethodTrampoline trampoline,
        IReadOnlyDictionary<string, MethodTrampoline> trampolines,
        bool prepare,
        IReadOnlyDictionary<Type, CecilWriter.ExternalPrototype>? externals)
    {
        ArgumentNullException.ThrowIfNull(signature);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(trampoline);
        ArgumentNullException.ThrowIfNull(trampolines);
        var map = new EmitMap(candidate => trampolines.TryGetValue(candidate.Name, out var target)
            ? target.Method
            : throw new ReplException($"no method '{candidate.Name}' is bound in the session"));
        return CompileMappedMethod(signature, state, trampoline, map, prepare, externals);
    }

    /// <summary>
    /// Compiles a method version using a session-method lookup evaluated only for references in its body.
    /// </summary>
    /// <param name="signature">The method's signature.</param>
    /// <param name="state">The validated body.</param>
    /// <param name="trampoline">The trampoline the version will be bound into.</param>
    /// <param name="map">The identities to use while emitting the body.</param>
    /// <param name="prepare">True to ask the JIT to compile the body.</param>
    /// <param name="externals">Prototypes of families emitted in the same group, or null.</param>
    /// <returns>The version, not yet bound.</returns>
    internal static CompiledMethodVersion CompileMappedMethod(
        MethodSignature signature,
        CellState state,
        MethodTrampoline trampoline,
        EmitMap map,
        bool prepare,
        IReadOnlyDictionary<Type, CecilWriter.ExternalPrototype>? externals = null)
    {
        ArgumentNullException.ThrowIfNull(signature);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(trampoline);
        ArgumentNullException.ThrowIfNull(map);
        state.ValidateMethodEnd();
        var name = signature.Name;
        var writer = new CecilWriter(SessionAssemblyKind.Methods);
        foreach (var (prototype, external) in externals ?? new Dictionary<Type, CecilWriter.ExternalPrototype>())
        {
            writer.DefineExternal(prototype, external);
        }

        var cell = writer.DefineType("IlRepl", "Cell",
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.Class | TypeAttributes.BeforeFieldInit,
            writer.Object);
        var returnType = writer.ImportSignature(
            signature.ReturnType,
            signature.ExactReturnType,
            signature.ReturnRequiredModifiers,
            signature.ReturnOptionalModifiers);
        var method = new MethodDefinition(
            name, MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig, returnType);
        for (var i = 0; i < signature.Parameters.Count; i++)
        {
            var parameter = signature.Parameters[i];
            var parameterType = writer.ImportSignature(
                parameter.Type,
                parameter.ExactType,
                parameter.RequiredModifiers,
                parameter.OptionalModifiers);
            method.Parameters.Add(new ParameterDefinition(
                parameter.Name ?? ("arg" + i.ToString(CultureInfo.InvariantCulture)),
                ParameterAttributes.None,
                parameterType));
        }

        cell.Methods.Add(method);
        try
        {
            CecilBodyEmitter.Emit(method, state, writer, map);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            throw new ReplException($"the runtime rejected method {name}: {ex.Message}", ex);
        }

        DefinitionAssembly definition;
        try
        {
            definition = writer.Load();
        }
        catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or ArgumentException or InvalidOperationException)
        {
            throw new ReplException($"the runtime rejected method {name}: {ex.Message} (the block is still open)", ex);
        }

        try
        {
            var body = definition.Assembly.GetType("IlRepl.Cell")?.GetMethod(name, BindingFlags.Public | BindingFlags.Static)
                ?? throw new ReplException($"the compiled type has no method {name}");
            if (prepare)
            {
                Prepare(body, name);
            }

            return new CompiledMethodVersion(definition, body, trampoline.DelegateType);
        }
        catch
        {
            SessionAssemblies.Release(definition);
            throw;
        }
    }

    private static void Prepare(MethodInfo body, string name)
    {
        try
        {
            MethodPreparation.Prepare(body);
        }
        catch (InvalidProgramException ex) when (ex.Message.Contains("Vararg", StringComparison.OrdinalIgnoreCase))
        {
            throw new ReplException(
                $"the runtime only supports the vararg calling convention on Windows; method {name} cannot be prepared here (the block " +
                $"is still open)", ex);
        }
        catch (InvalidProgramException ex)
        {
            throw new ReplException(
                $"the JIT rejected method {name}: {ex.Message} " +
                $"(check .show for a stack mismatch between branches; the block is still open)", ex);
        }
        catch (Exception ex) when (Binding.ReplRecovery.IsRecoverable(ex))
        {
            // Preparation may activate the module. Reopening bypasses this execution boundary.
            throw new ReplException($"the runtime rejected method {name}: {ex.Message} (the block is still open)", ex);
        }
    }
}
