using System.Globalization;
using System.Reflection;
using Mono.Cecil;
using MethodAttributes = Mono.Cecil.MethodAttributes;
using ParameterAttributes = Mono.Cecil.ParameterAttributes;
using TypeAttributes = Mono.Cecil.TypeAttributes;

namespace IlRepl.Engine;

/// <summary>
/// Exports session definitions and the current cell without executing user code or retaining live-session references.
/// </summary>
public static class AssemblyExporter
{
    /// <summary>
    /// Writes the session to a file.
    /// </summary>
    /// <param name="session">The session.</param>
    /// <param name="path">The output path; the assembly is named after the file.</param>
    /// <exception cref="ReplException">A block is open, the cell is incomplete, or the writer refused it.</exception>
    public static void Save(Session session, string path) => SaveCancellable(session, path, CancellationToken.None);

    /// <summary>
    /// Atomically replaces an assembly file after cancellable export preparation has completed.
    /// </summary>
    /// <param name="session">The session to export.</param>
    /// <param name="path">The destination file, whose basename determines the assembly name.</param>
    /// <param name="cancellationToken">Cancels preparation or writing before the destination is replaced.</param>
    internal static void SaveCancellable(Session session, string path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = Path.GetFullPath(path);
        if (Path.EndsInDirectorySeparator(path) || Directory.Exists(fullPath))
        {
            throw new ArgumentException("The assembly destination must be a file.", nameof(path));
        }

        var assemblyName = Path.GetFileNameWithoutExtension(fullPath);
        if (string.IsNullOrWhiteSpace(assemblyName))
        {
            throw new ArgumentException("The assembly destination must have a filename.", nameof(path));
        }

        var image = WriteCancellable(session, assemblyName, cancellationToken);
        AtomicAssemblyFile.Write(fullPath, image, cancellationToken);
    }

    /// <summary>
    /// Writes the session to a PE image.
    /// </summary>
    /// <param name="session">The session.</param>
    /// <param name="assemblyName">The assembly's simple name.</param>
    /// <returns>The image.</returns>
    /// <exception cref="ReplException">A block is open, the cell is incomplete, or the writer refused it.</exception>
    public static byte[] Write(Session session, string assemblyName)
        => WriteCancellable(session, assemblyName, CancellationToken.None);

    /// <summary>
    /// Produces an assembly image while observing cancellation between emitted definitions.
    /// </summary>
    /// <param name="session">The session to export.</param>
    /// <param name="assemblyName">The assembly's simple name.</param>
    /// <param name="cancellationToken">Cancels export preparation before publication.</param>
    /// <returns>The completed assembly image.</returns>
    internal static byte[] WriteCancellable(Session session, string assemblyName, CancellationToken cancellationToken)
        => WriteCore(session, assemblyName, null, false, null, cancellationToken: cancellationToken);

    /// <summary>
    /// Writes committed declarations for inspecting an unfinished current cell as ILAsm source.
    /// </summary>
    /// <param name="session">The session whose committed declarations are captured.</param>
    /// <returns>The declaration image without a current-cell entry point.</returns>
    internal static byte[] WriteDeclarations(Session session)
        => WriteCore(session, "ilrepl_cell", null, false, null, includeCell: false);

    /// <summary>
    /// Writes a comparison snapshot without executing or exporting the current cell body.
    /// </summary>
    /// <param name="session">The session declarations to capture.</param>
    /// <param name="edit">The selected edit.</param>
    /// <param name="original">Whether selected calls bind to its captured original.</param>
    /// <param name="instrument">Adds invocation observation to the selected method before the image is written.</param>
    /// <param name="complete">Completes call-site instrumentation after all method bodies have been emitted.</param>
    /// <returns>The frozen comparison assembly.</returns>
    internal static byte[] WriteComparison(
        Session session,
        MethodEdit edit,
        bool original,
        Action<CecilWriter, MethodDefinition> instrument,
        Action<CecilWriter>? complete = null)
        => WriteCore(session, "IlReplComparison", edit, original, instrument, complete: complete);

    private static byte[] WriteCore(
        Session session,
        string assemblyName,
        MethodEdit? comparison,
        bool original,
        Action<CecilWriter, MethodDefinition>? instrument,
        bool includeCell = true,
        Action<CecilWriter>? complete = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyName);
        cancellationToken.ThrowIfCancellationRequested();
        if (comparison is null && includeCell)
        {
            CellCompiler.RequireComplete(session);
        }

        var writer = new CecilWriter(assemblyName);
        var trampolines = session.Methods.ToDictionary(m => m.Signature.Name, m => m.Trampoline, StringComparer.Ordinal);
        try
        {
            // The cell type's method signatures exist before any body is written, so a type's
            // call to a session method binds to the exported method, and the type is added to the
            // module after the families so it follows them in the metadata.
            var cell = new TypeDefinition("IlRepl", "Cell", TypeAttributes.Public | TypeAttributes.Abstract
                | TypeAttributes.Sealed | TypeAttributes.Class | TypeAttributes.BeforeFieldInit, writer.Object);
            var map = new EmitMap(signature => trampolines.TryGetValue(signature.Name, out var t) ? t.Method
                : throw new ReplException($"no method '{signature.Name}' is bound in the session"));
            var methods = new List<(SessionMethod Method, MethodDefinition Definition)>();
            foreach (var method in session.Methods)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var definition = new MethodDefinition(method.Signature.Name,
                    MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig, writer.Module.TypeSystem.Void);
                cell.Methods.Add(definition);
                // A call to the session method binds to its trampoline; here that is the method itself.
                writer.Define(method.Trampoline.Method, definition);
                methods.Add((method, definition));
            }

            var revisions = new List<(ImportedMethodFamily Revision, Dictionary<MemberInfo, IMemberDefinition> Definitions)>();
            foreach (var edit in session.Edits)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (edit == comparison && original)
                {
                    if (edit.Baseline.Problems.Count == 0)
                    {
                        var definitions = edit.Baseline.Write(writer);
                        revisions.Add((edit.Current!, definitions));
                    }
                    else
                    {
                        var definitions = edit.Current!.Write(writer);
                        CecilOriginalCall.Replace(edit, (MethodDefinition)definitions[edit.Original.Method], writer);
                        revisions.Add((edit.Current!, definitions));
                    }
                }
                else
                {
                    if (edit.Current is { } current)
                    {
                        revisions.Add((current, current.Write(writer)));
                    }
                }
            }

            // A copy can use another edit as its source; callers still bind each committed revision to its own definitions.
            foreach (var (revision, definitions) in revisions)
            {
                ImportedMethodFamily.DefineRevisionReferences(writer, revision, definitions);
            }

            if (comparison is not null)
            {
                instrument!(writer, (MethodDefinition)writer.Import(IlAsmRenderer.DefinitionOf(comparison.Method!)));
            }

            TypeEmitter.WriteAllCancellable(writer,
                [.. session.Types.Select(f => (f.Declaration, f.Prototypes, RuntimeTypes(f)))],
                trampolines, cancellationToken);

            // Signatures are imported once every session type is a definition of this module.
            foreach (var (method, definition) in methods)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var signature = method.Signature;
                definition.ReturnType = writer.ImportSignature(
                    signature.ReturnType,
                    signature.ExactReturnType,
                    signature.ReturnRequiredModifiers,
                    signature.ReturnOptionalModifiers);
                for (var i = 0; i < signature.Parameters.Count; i++)
                {
                    var parameter = signature.Parameters[i];
                    var type = writer.ImportSignature(
                        parameter.Type,
                        parameter.ExactType,
                        parameter.RequiredModifiers,
                        parameter.OptionalModifiers);
                    definition.Parameters.Add(new ParameterDefinition(
                        parameter.Name ?? ("arg" + i.ToString(CultureInfo.InvariantCulture)),
                        ParameterAttributes.None,
                        type));
                }
            }

            writer.Module.Types.Add(cell);
            foreach (var (method, definition) in methods)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Guarded("method " + method.Signature.Name, () => CecilBodyEmitter.Emit(definition, method.State, writer, map));
            }

            if (comparison is null && includeCell)
            {
                WriteRun(writer, cell, session, map);
            }

            cancellationToken.ThrowIfCancellationRequested();
            complete?.Invoke(writer);
            var image = writer.Write();
            cancellationToken.ThrowIfCancellationRequested();
            return image;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException or NullReferenceException)
        {
            throw new ReplException("the writer rejected the session: " + ex.Message, ex);
        }
    }

    private static void WriteRun(CecilWriter writer, TypeDefinition cell, Session session, EmitMap map)
    {
        var state = session.Cell;
        var run = new MethodDefinition("Run",
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig, writer.Object);
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
            var type = argument.ExactType is null ? writer.Import(argument.Type) : writer.Import(argument.ExactType);
            run.Parameters.Add(new ParameterDefinition(
                argument.Name ?? ("arg" + i.ToString(CultureInfo.InvariantCulture)),
                ParameterAttributes.None,
                type));
        }

        Guarded("the cell", () => CecilBodyEmitter.Emit(run, state, writer, map));
        if (state.IsVarArg)
        {
            // Reflection cannot invoke a vararg method, so a standard-convention Invoke forwards
            // the fixed arguments and an empty variable-argument list, as the live cell has.
            var invoke = new MethodDefinition("Invoke",
                MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig, writer.Object);
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

    // The emitter takes the runtime types as optional, because a family that is still being built has none yet.
    private static IReadOnlyDictionary<string, Type>? RuntimeTypes(SessionType family) => family.Types;

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
