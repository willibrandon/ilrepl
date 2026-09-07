using System.Reflection;
using System.Reflection.Emit;

namespace IlRepl.Engine;

/// <summary>
/// Compiles a session's cell into a dynamic assembly, or persists it to disk as a real assembly.
/// A running cell's type carries <c>Run</c> alone and calls session methods through their
/// trampolines; a persisted assembly carries one static method per <c>.method</c> definition
/// beside <c>Run</c>, with calls bound directly. Every compile replays the cell's lines against a
/// fresh <see cref="CellState"/> so generic parameters bind to the method being emitted.
/// </summary>
public static class CellCompiler
{

    /// <summary>
    /// Compiles the cell for execution.
    /// </summary>
    /// <param name="session">The session holding the cell.</param>
    /// <returns>The compiled cell.</returns>
    /// <exception cref="ReplException">The cell is incomplete or the runtime rejected it.</exception>
    public static CompiledCell Compile(Session session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var name = SessionAssemblies.NextName(SessionAssemblyKind.Cell);
        // Collectible assemblies let CoreCLR unload old cells; the browser runtime has no unloading.
        var access = OperatingSystem.IsBrowser() ? AssemblyBuilderAccess.Run : AssemblyBuilderAccess.RunAndCollect;
        var context = SessionAssemblies.CreateCellContext(name);
        AssemblyBuilder assembly;
        if (context is null)
        {
            assembly = AssemblyBuilder.DefineDynamicAssembly(SessionAssemblies.MakeAssemblyName(name), access);
        }
        else
        {
            // The dynamic assembly lands in the cell's own context, so a session name typed into
            // the cell resolves through that context.
            using var scope = context.EnterContextualReflection();
            assembly = AssemblyBuilder.DefineDynamicAssembly(SessionAssemblies.MakeAssemblyName(name), access);
        }

        return Build(session, assembly, name, context);
    }

    /// <summary>
    /// Writes the cell to disk as an assembly containing <c>IlRepl.Cell.Run</c>.
    /// </summary>
    /// <param name="session">The session holding the cell.</param>
    /// <param name="path">The output path; the assembly name is the file name without extension.</param>
    /// <exception cref="ReplException">The cell is incomplete or the runtime rejected it.</exception>
    public static void Save(Session session, string path)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var assemblyName = Path.GetFileNameWithoutExtension(path);
        if (assemblyName.Length == 0)
        {
            assemblyName = "ilrepl_cell";
        }

        var assembly = new PersistedAssemblyBuilder(new AssemblyName(assemblyName), typeof(object).Assembly);
        Build(session, assembly, assemblyName, null);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        assembly.Save(fullPath);
    }

    private static CompiledCell Build(Session session, AssemblyBuilder assembly, string moduleName, DefinitionLoadContext? context)
    {
        if (session.OpenMethod is { } open)
        {
            throw new ReplException($"method {open.Name} is still open; close it with }}");
        }

        if (session.OpenType is { } openType)
        {
            throw new ReplException($"class {openType} is still open; close it with }}");
        }

        var cell = session.Cell;
        var pending = cell.ReferencedLabels().Where(l => !cell.DefinedLabels.Contains(l)).Distinct().ToList();
        if (pending.Count > 0)
        {
            throw new ReplException($"label{(pending.Count > 1 ? "s" : "")} referenced but never defined: {string.Join(", ", pending)} (define with 'NAME:')");
        }

        if (cell.OpenBlockDepth > 0)
        {
            throw new ReplException("a protected region is still open; close it with }");
        }

        if (cell.Stack.Count > 1)
        {
            throw new ReplException($"the stack must hold 0 or 1 value at the end of the cell, but has {cell.Stack.Count}: {cell.Stack.Render()}  (pop, or stloc into a local)");
        }

        var module = assembly.DefineDynamicModule(moduleName);
        var type = DefineCellType(module);
        var persisted = assembly is PersistedAssemblyBuilder;
        var methods = persisted
            ? DefineSessionMethods(type, session.Methods)
            : session.Methods.ToDictionary(m => m.Signature.Name, m => m.Trampoline.Method, StringComparer.Ordinal);
        var convention = cell.IsVarArg ? CallingConventions.VarArgs : CallingConventions.Standard;
        var run = type.DefineMethod("Run", MethodAttributes.Public | MethodAttributes.Static, convention, typeof(object), Type.EmptyTypes);
        var names = session.TypeParameterNames;
        Type[] genericParameters = names.Count > 0 ? run.DefineGenericParameters([.. names]) : [];

        var signatures = session.Methods.Select(m => m.Signature).ToArray();
        var state = new CellState(session.Resolver, new GenericContext([], genericParameters), signatures, null, false);
        foreach (var line in session.DeclarationLines)
        {
            state.Apply(line);
        }

        foreach (var line in session.BodyLines)
        {
            state.Apply(line);
        }

        var parameterTypes = state.Arguments.Select(a => a.Type).ToArray();
        run.SetParameters(parameterTypes);
        for (var i = 0; i < state.Arguments.Count; i++)
        {
            run.DefineParameter(i + 1, ParameterAttributes.None, state.Arguments[i].Name ?? ("arg" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        if (persisted)
        {
            EmitSessionMethods(methods, session.Methods);
        }

        EmitGuarded("the cell", () => EmitBody(run.GetILGenerator(), state, methods));

        MethodBuilder entry = run;
        if (state.IsVarArg)
        {
            entry = EmitVarArgWrapper(type, run, names, parameterTypes);
        }

        var created = CreateCellType(type, "the cell");
        var method = created.GetMethod(entry.Name, BindingFlags.Public | BindingFlags.Static)
            ?? throw new ReplException("the compiled cell has no entry point");
        var dependencies = session.Methods.Select(m => m.Trampoline.Definition).Distinct().ToArray();
        var definition = SessionAssemblies.RegisterCell(assembly, created, dependencies, context);
        return new CompiledCell(assembly, created, method, state.Arguments.Select(a => a.Value).ToArray(), definition);
    }

    private static TypeBuilder DefineCellType(ModuleBuilder module) =>
        module.DefineType("IlRepl.Cell", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.Class | TypeAttributes.BeforeFieldInit);

    private static Dictionary<string, MethodInfo> DefineSessionMethods(TypeBuilder type, IReadOnlyList<SessionMethod> methods)
    {
        // A persisted assembly carries the session methods itself. Every signature exists before
        // any body is emitted, which is what lets one body call another, or itself.
        var builders = new Dictionary<string, MethodInfo>(StringComparer.Ordinal);
        foreach (var method in methods)
        {
            var signature = method.Signature;
            var builder = type.DefineMethod(signature.Name, MethodAttributes.Public | MethodAttributes.Static, CallingConventions.Standard, signature.ReturnType, signature.ParameterTypes);
            for (var i = 0; i < signature.Parameters.Count; i++)
            {
                builder.DefineParameter(i + 1, ParameterAttributes.None, signature.Parameters[i].Name ?? ("arg" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }

            builders[signature.Name] = builder;
        }

        return builders;
    }

    private static void EmitSessionMethods(Dictionary<string, MethodInfo> builders, IReadOnlyList<SessionMethod> methods)
    {
        foreach (var method in methods)
        {
            EmitGuarded("method " + method.Signature.Name, () => EmitBody(((MethodBuilder)builders[method.Signature.Name]).GetILGenerator(), method.State, builders));
        }
    }

    private static void EmitGuarded(string what, Action emit)
    {
        // ILGenerator refuses some operands only while emitting. That is a verdict on the body
        // and must reach the user as an error, never as an exception out of the session.
        try
        {
            emit();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            throw new ReplException($"the runtime rejected {what}: {ex.Message}", ex);
        }
    }

    private static Type CreateCellType(TypeBuilder type, string what)
    {
        try
        {
            return type.CreateType();
        }
        catch (Exception ex) when (ex is InvalidOperationException or TypeLoadException or NotSupportedException or ArgumentException)
        {
            throw new ReplException($"the runtime rejected {what}: " + ex.Message, ex);
        }
    }

    private static void EmitBody(ILGenerator il, CellState state, IReadOnlyDictionary<string, MethodInfo> methods)
    {
        var locals = state.Locals.Select(l => il.DeclareLocal(l.Type, l.IsPinned)).ToArray();
        var labels = state.DefinedLabels.ToDictionary(l => l, _ => il.DefineLabel(), StringComparer.Ordinal);

        foreach (var entry in state.Entries)
        {
            foreach (var l in entry.Labels)
            {
                il.MarkLabel(labels[l]);
            }

            switch (entry.Kind)
            {
                case EntryKind.Instruction:
                    InstructionEmitter.Emit(il, entry.Instruction!, locals, labels, methods);
                    break;
                case EntryKind.Block:
                    EmitBlock(il, entry);
                    break;
                default:
                    break;
            }
        }

        if (state.LastInstructionEndsFlow)
        {
            return;
        }

        if (state.IsMethod)
        {
            // The close validated the stack against the return type, so the implied ret is bare.
            il.Emit(OpCodes.Ret);
            return;
        }

        var top = state.Stack.Top;
        if (state.Stack.Count == 0)
        {
            il.Emit(OpCodes.Ldnull);
        }
        else if (top is not null && top != typeof(NullReferenceMarker) && top.IsValueType)
        {
            il.Emit(OpCodes.Box, top);
        }

        il.Emit(OpCodes.Ret);
    }

    private static void EmitBlock(ILGenerator il, CellEntry entry)
    {
        switch (entry.Block)
        {
            case BlockKind.Try:
                il.BeginExceptionBlock();
                break;
            case BlockKind.Catch:
                il.BeginCatchBlock(entry.CatchType ?? typeof(object));
                break;
            case BlockKind.Filter:
                il.BeginExceptFilterBlock();
                break;
            case BlockKind.FilterHandler:
                il.BeginCatchBlock(null);
                break;
            case BlockKind.Finally:
                il.BeginFinallyBlock();
                break;
            case BlockKind.Fault:
                il.BeginFaultBlock();
                break;
            case BlockKind.End:
                il.EndExceptionBlock();
                break;
            default:
                break;
        }
    }

    private static MethodBuilder EmitVarArgWrapper(TypeBuilder type, MethodBuilder run, IReadOnlyList<string> names, Type[] parameterTypes)
    {
        // Reflection cannot invoke a vararg method directly, so a standard-convention wrapper
        // forwards the fixed arguments and an empty variable-argument list.
        var wrapper = type.DefineMethod("Invoke", MethodAttributes.Public | MethodAttributes.Static, CallingConventions.Standard, typeof(object), Type.EmptyTypes);
        MethodInfo target = run;
        if (names.Count > 0)
        {
            var wrapperParameters = wrapper.DefineGenericParameters([.. names]);
            target = run.MakeGenericMethod(wrapperParameters);
        }

        wrapper.SetParameters(parameterTypes);
        var il = wrapper.GetILGenerator();
        for (var i = 0; i < parameterTypes.Length; i++)
        {
            il.Emit(OpCodes.Ldarg, (short)i);
        }

        il.EmitCall(OpCodes.Call, target, Type.EmptyTypes);
        il.Emit(OpCodes.Ret);
        return wrapper;
    }
}
