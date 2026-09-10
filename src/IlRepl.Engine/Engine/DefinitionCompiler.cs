using System.Reflection;
using Mono.Cecil;
using MethodAttributes = Mono.Cecil.MethodAttributes;
using ParameterAttributes = Mono.Cecil.ParameterAttributes;
using TypeAttributes = Mono.Cecil.TypeAttributes;

namespace IlRepl.Engine;

/// <summary>
/// Compiles the definitions a session keeps: a method body becomes a version assembly whose
/// calls to other session methods go through their trampolines. Compilation writes, loads, and
/// where the runtime supports it prepares the body on the JIT, and never invokes anything, so a
/// failure is a verdict the session can report while its block stays open.
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
    public static CompiledMethodVersion CompileMethod(MethodSignature signature, CellState state, MethodTrampoline trampoline, IReadOnlyDictionary<string, MethodTrampoline> trampolines, bool prepare) =>
        CompileMethod(signature, state, trampoline, trampolines, prepare, null);

    /// <summary>
    /// Compiles one version of a session method whose body may reference prototypes of families
    /// written in the same group.
    /// </summary>
    /// <param name="signature">The method's signature.</param>
    /// <param name="state">The validated body.</param>
    /// <param name="trampoline">The trampoline the version will be bound into.</param>
    /// <param name="trampolines">The trampolines of every session method the body may call, by name, including this one.</param>
    /// <param name="prepare">True to ask the JIT to compile the body.</param>
    /// <param name="externals">Prototypes of families in the group, referenced by their assembly names, or null.</param>
    /// <returns>The version, not yet bound.</returns>
    /// <exception cref="ReplException">The emitter, the loader, or the JIT rejected the body.</exception>
    public static CompiledMethodVersion CompileMethod(MethodSignature signature, CellState state, MethodTrampoline trampoline, IReadOnlyDictionary<string, MethodTrampoline> trampolines, bool prepare, IReadOnlyDictionary<Type, CecilWriter.ExternalPrototype>? externals)
    {
        ArgumentNullException.ThrowIfNull(signature);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(trampoline);
        ArgumentNullException.ThrowIfNull(trampolines);
        state.ValidateMethodEnd();
        var name = signature.Name;
        var writer = new CecilWriter(SessionAssemblyKind.Methods);
        foreach (var (prototype, external) in externals ?? new Dictionary<Type, CecilWriter.ExternalPrototype>())
        {
            writer.DefineExternal(prototype, external);
        }

        var cell = writer.DefineType("IlRepl", "Cell", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.Class | TypeAttributes.BeforeFieldInit, writer.Object);
        var method = new MethodDefinition(name, MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig, writer.Import(signature.ReturnType));
        for (var i = 0; i < signature.Parameters.Count; i++)
        {
            var parameter = signature.Parameters[i];
            method.Parameters.Add(new ParameterDefinition(parameter.Name ?? ("arg" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)), ParameterAttributes.None, writer.Import(parameter.Type)));
        }

        cell.Methods.Add(method);
        var map = new EmitMap(s => trampolines.TryGetValue(s.Name, out var t) ? t.Method : throw new ReplException($"no method '{s.Name}' is bound in the session"));
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

            var implementation = Delegate.CreateDelegate(trampoline.DelegateType, body);
            return new CompiledMethodVersion(definition, body, implementation);
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
            throw new ReplException($"the runtime only supports the vararg calling convention on Windows; method {name} cannot be prepared here (the block is still open)", ex);
        }
        catch (InvalidProgramException ex)
        {
            throw new ReplException($"the JIT rejected method {name}: {ex.Message} (check .show for a stack mismatch between branches; the block is still open)", ex);
        }
        catch (Exception ex)
        {
            // Preparation never runs user code, so anything else it throws (a missing member
            // behind callvirt on a static, a type that fails to load) is also a verdict on the
            // body, and the session must be able to keep the block open.
            throw new ReplException($"the runtime rejected method {name}: {ex.Message} (the block is still open)", ex);
        }
    }
}
