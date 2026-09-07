using Mono.Cecil;
using MethodAttributes = Mono.Cecil.MethodAttributes;
using ParameterAttributes = Mono.Cecil.ParameterAttributes;
using TypeAttributes = Mono.Cecil.TypeAttributes;

namespace IlRepl.Engine;

/// <summary>
/// Writes a session to disk as one assembly: every type family in declaration order, then
/// <c>IlRepl.Cell</c> with the session methods and <c>Run</c>. The same writer produces the
/// live definitions, so the saved metadata is what the session ran; calls between session
/// methods become direct calls, with no trampolines or delegates.
/// </summary>
public static class AssemblyExporter
{
    /// <summary>
    /// Writes the session to a file.
    /// </summary>
    /// <param name="session">The session.</param>
    /// <param name="path">The output path; the assembly is named after the file.</param>
    /// <exception cref="ReplException">A block is open, the cell is incomplete, or the writer refused it.</exception>
    public static void Save(Session session, string path)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var assemblyName = Path.GetFileNameWithoutExtension(path);
        if (assemblyName.Length == 0)
        {
            assemblyName = "ilrepl_cell";
        }

        var image = Write(session, assemblyName);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllBytes(fullPath, image);
    }

    /// <summary>
    /// Writes the session to a PE image.
    /// </summary>
    /// <param name="session">The session.</param>
    /// <param name="assemblyName">The assembly's simple name.</param>
    /// <returns>The image.</returns>
    /// <exception cref="ReplException">A block is open, the cell is incomplete, or the writer refused it.</exception>
    public static byte[] Write(Session session, string assemblyName)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyName);
        CellCompiler.RequireComplete(session);
        var writer = new CecilWriter(assemblyName);
        var trampolines = session.Methods.ToDictionary(m => m.Signature.Name, m => m.Trampoline, StringComparer.Ordinal);
        try
        {
            // The cell type's method signatures exist before any body is written, so a type's
            // call to a session method binds to the exported method, and the type is added to the
            // module after the families so it follows them in the metadata.
            var cell = new TypeDefinition("IlRepl", "Cell", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.Class | TypeAttributes.BeforeFieldInit, writer.Object);
            var map = new EmitMap(signature => trampolines.TryGetValue(signature.Name, out var t) ? t.Method : throw new ReplException($"no method '{signature.Name}' is bound in the session"));
            var methods = new List<(SessionMethod Method, MethodDefinition Definition)>();
            foreach (var method in session.Methods)
            {
                var definition = new MethodDefinition(method.Signature.Name, MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig, writer.Module.TypeSystem.Void);
                cell.Methods.Add(definition);
                // A call to the session method binds to its trampoline; here that is the method itself.
                writer.Define(method.Trampoline.Method, definition);
                methods.Add((method, definition));
            }

            foreach (var family in session.Types)
            {
                TypeEmitter.Write(writer, family.Declaration, family.Prototypes, trampolines, family.Types);
            }

            // Signatures are imported once every session type is a definition of this module.
            foreach (var (method, definition) in methods)
            {
                var signature = method.Signature;
                definition.ReturnType = writer.Import(signature.ReturnType);
                for (var i = 0; i < signature.Parameters.Count; i++)
                {
                    var parameter = signature.Parameters[i];
                    definition.Parameters.Add(new ParameterDefinition(parameter.Name ?? ("arg" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)), ParameterAttributes.None, writer.Import(parameter.Type)));
                }
            }

            writer.Module.Types.Add(cell);
            foreach (var (method, definition) in methods)
            {
                Guarded("method " + method.Signature.Name, () => CecilBodyEmitter.Emit(definition, method.State, writer, map));
            }

            WriteRun(writer, cell, session, map);
            return writer.Write();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException or NullReferenceException)
        {
            throw new ReplException("the writer rejected the session: " + ex.Message, ex);
        }
    }

    private static void WriteRun(CecilWriter writer, TypeDefinition cell, Session session, EmitMap map)
    {
        var state = session.Cell;
        var run = new MethodDefinition("Run", MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig, writer.Object);
        cell.Methods.Add(run);
        var names = session.TypeParameterNames;
        var cellParameters = state.Generics.MethodArguments;
        for (var i = 0; i < names.Count; i++)
        {
            var parameter = new GenericParameter(names[i], run);
            run.GenericParameters.Add(parameter);
            if (i < cellParameters.Count)
            {
                writer.Define(cellParameters[i], parameter);
            }
        }

        if (state.IsVarArg)
        {
            run.CallingConvention = MethodCallingConvention.VarArg;
        }

        for (var i = 0; i < state.Arguments.Count; i++)
        {
            var argument = state.Arguments[i];
            run.Parameters.Add(new ParameterDefinition(argument.Name ?? ("arg" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)), ParameterAttributes.None, writer.Import(argument.Type)));
        }

        Guarded("the cell", () => CecilBodyEmitter.Emit(run, state, writer, map));
        if (state.IsVarArg)
        {
            // Reflection cannot invoke a vararg method, so a standard-convention Invoke forwards
            // the fixed arguments and an empty variable-argument list, as the live cell has.
            var invoke = new MethodDefinition("Invoke", MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig, writer.Object);
            cell.Methods.Add(invoke);
            MethodReference target = run;
            if (names.Count > 0)
            {
                var generic = new GenericInstanceMethod(run);
                for (var i = 0; i < names.Count; i++)
                {
                    var parameter = new GenericParameter(names[i], invoke);
                    invoke.GenericParameters.Add(parameter);
                    generic.GenericArguments.Add(parameter);
                }

                target = generic;
            }

            foreach (var parameter in run.Parameters)
            {
                invoke.Parameters.Add(new ParameterDefinition(parameter.Name, ParameterAttributes.None, parameter.ParameterType));
            }

            var il = invoke.Body.GetILProcessor();
            for (var i = 0; i < run.Parameters.Count; i++)
            {
                il.Emit(Mono.Cecil.Cil.OpCodes.Ldarg, invoke.Parameters[i]);
            }

            var site = new MethodReference(target.Name, target.ReturnType, target.DeclaringType)
            {
                HasThis = false,
                CallingConvention = MethodCallingConvention.VarArg,
            };
            foreach (var parameter in run.Parameters)
            {
                site.Parameters.Add(new ParameterDefinition(parameter.ParameterType));
            }

            il.Emit(Mono.Cecil.Cil.OpCodes.Call, target is GenericInstanceMethod ? target : site);
            il.Emit(Mono.Cecil.Cil.OpCodes.Ret);
        }
    }

    private static void Guarded(string what, Action emit)
    {
        try
        {
            emit();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            throw new ReplException($"the writer rejected {what}: {ex.Message}", ex);
        }
    }
}
