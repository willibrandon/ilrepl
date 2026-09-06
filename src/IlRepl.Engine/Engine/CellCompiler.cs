using System.Reflection;
using System.Reflection.Emit;

namespace IlRepl.Engine;

/// <summary>
/// Compiles a session's cell into a dynamic assembly with a single static method, or persists
/// it to disk as a real assembly. Every compile replays the accepted lines against a fresh
/// <see cref="CellState"/> so generic parameters bind to the method being emitted.
/// </summary>
public static class CellCompiler
{
    private static int s_counter;

    /// <summary>
    /// Compiles the cell for execution.
    /// </summary>
    /// <param name="session">The session holding the cell.</param>
    /// <returns>The compiled cell.</returns>
    /// <exception cref="ReplException">The cell is incomplete or the runtime rejected it.</exception>
    public static CompiledCell Compile(Session session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var id = Interlocked.Increment(ref s_counter);
        var name = new AssemblyName("ilrepl.cell" + id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        // Collectible assemblies let CoreCLR unload old cells; the browser runtime has no unloading.
        var access = OperatingSystem.IsBrowser() ? AssemblyBuilderAccess.Run : AssemblyBuilderAccess.RunAndCollect;
        var assembly = AssemblyBuilder.DefineDynamicAssembly(name, access);
        return Build(session, assembly, name.Name!);
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
        Build(session, assembly, assemblyName);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        assembly.Save(fullPath);
    }

    private static CompiledCell Build(Session session, AssemblyBuilder assembly, string moduleName)
    {
        var pending = session.State.ReferencedLabels().Where(l => !session.State.DefinedLabels.Contains(l)).Distinct().ToList();
        if (pending.Count > 0)
        {
            throw new ReplException($"label{(pending.Count > 1 ? "s" : "")} referenced but never defined: {string.Join(", ", pending)} (define with 'NAME:')");
        }

        if (session.State.OpenBlockDepth > 0)
        {
            throw new ReplException("a protected region is still open; close it with }");
        }

        if (session.State.Stack.Count > 1)
        {
            throw new ReplException($"the stack must hold 0 or 1 value at the end of the cell, but has {session.State.Stack.Count}: {session.State.Stack.Render()}  (pop, or stloc into a local)");
        }

        var module = assembly.DefineDynamicModule(moduleName);
        var type = module.DefineType("IlRepl.Cell", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.Class | TypeAttributes.BeforeFieldInit);
        var convention = session.State.IsVarArg ? CallingConventions.VarArgs : CallingConventions.Standard;
        var run = type.DefineMethod("Run", MethodAttributes.Public | MethodAttributes.Static, convention, typeof(object), Type.EmptyTypes);
        var names = session.TypeParameterNames;
        Type[] genericParameters = names.Count > 0 ? run.DefineGenericParameters([.. names]) : [];

        var state = new CellState(session.Resolver, new GenericContext([], genericParameters));
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

        EmitBody(run.GetILGenerator(), state);

        MethodBuilder entry = run;
        if (state.IsVarArg)
        {
            entry = EmitVarArgWrapper(type, run, names, parameterTypes);
        }

        Type created;
        try
        {
            created = type.CreateType();
        }
        catch (Exception ex) when (ex is InvalidOperationException or TypeLoadException or NotSupportedException or ArgumentException)
        {
            throw new ReplException("the runtime rejected the cell: " + ex.Message, ex);
        }

        var method = created.GetMethod(entry.Name, BindingFlags.Public | BindingFlags.Static)
            ?? throw new ReplException("the compiled cell has no entry point");
        return new CompiledCell(assembly, created, method, state.Arguments.Select(a => a.Value).ToArray());
    }

    private static void EmitBody(ILGenerator il, CellState state)
    {
        var locals = state.Locals.Select(l => il.DeclareLocal(l.Type, l.IsPinned)).ToArray();
        var labels = state.DefinedLabels.ToDictionary(l => l, _ => il.DefineLabel(), StringComparer.Ordinal);

        CellEntry? lastInstruction = null;
        foreach (var entry in state.Entries)
        {
            foreach (var l in entry.Labels)
            {
                il.MarkLabel(labels[l]);
            }

            switch (entry.Kind)
            {
                case EntryKind.Instruction:
                    InstructionEmitter.Emit(il, entry.Instruction!, locals, labels);
                    lastInstruction = entry;
                    break;
                case EntryKind.Block:
                    EmitBlock(il, entry);
                    lastInstruction = null;
                    break;
                default:
                    break;
            }
        }

        if (lastInstruction?.Instruction is { } last && EndsFlow(last.Op))
        {
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

    private static bool EndsFlow(OpCode op) =>
        op.FlowControl is FlowControl.Return or FlowControl.Throw || op == OpCodes.Br || op == OpCodes.Br_S || op == OpCodes.Jmp;

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
