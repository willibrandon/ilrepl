using System.Reflection;
using System.Reflection.Emit;

namespace IlRepl.Engine;

/// <summary>
/// Compiles a session's cell into a dynamic assembly whose type carries <c>Run</c> alone and
/// calls session methods through their trampolines. Every compile replays the cell's lines
/// against a fresh <see cref="CellState"/> so generic parameters bind to the method being
/// emitted. Saving to disk is <see cref="AssemblyExporter"/>'s job.
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
    /// Refuses a session whose cell cannot be compiled: an open block, a pending label, an open
    /// protected region, or more than one value on the stack.
    /// </summary>
    /// <param name="session">The session.</param>
    /// <exception cref="ReplException">The cell is incomplete.</exception>
    public static void RequireComplete(Session session)
    {
        ArgumentNullException.ThrowIfNull(session);
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
    }

    private static CompiledCell Build(Session session, AssemblyBuilder assembly, string moduleName, DefinitionLoadContext? context)
    {
        RequireComplete(session);
        var cell = session.Cell;
        var module = assembly.DefineDynamicModule(moduleName);
        var type = DefineCellType(module);
        var methods = session.Methods.ToDictionary(m => m.Signature.Name, m => m.Trampoline.Method, StringComparer.Ordinal);
        var convention = cell.IsVarArg ? CallingConventions.VarArgs : CallingConventions.Standard;
        var run = type.DefineMethod("Run", MethodAttributes.Public | MethodAttributes.Static, convention, typeof(object), Type.EmptyTypes);
        var names = session.TypeParameterNames;
        Type[] genericParameters = names.Count > 0 ? run.DefineGenericParameters([.. names]) : [];

        var signatures = session.Methods.Select(m => m.Signature).ToArray();
        var state = new CellState(session.Resolver, new GenericContext([], genericParameters), signatures, null, false, session.TypeTable, null);
        foreach (var line in session.DeclarationLines)
        {
            state.Apply(line);
        }

        foreach (var line in session.BodyLines)
        {
            state.Apply(line);
        }

        // Session types are checked by the REPL, not the runtime: the cell skips access checks
        // for every session assembly it mentions, and holds those assemblies while it lives.
        var typeDependencies = SessionMentions.Definitions(state).ToList();
        AccessGrants.Grant(assembly, module, typeDependencies);

        var parameterTypes = state.Arguments.Select(a => a.Type).ToArray();
        run.SetParameters(parameterTypes);
        for (var i = 0; i < state.Arguments.Count; i++)
        {
            run.DefineParameter(i + 1, ParameterAttributes.None, state.Arguments[i].Name ?? ("arg" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        EmitGuarded("the cell", () => EmitBody(run.GetILGenerator(), state, methods));

        var entry = run;
        if (state.IsVarArg)
        {
            entry = EmitVarArgWrapper(type, run, names, parameterTypes);
        }

        var created = CreateCellType(type, "the cell");
        var method = created.GetMethod(entry.Name, BindingFlags.Public | BindingFlags.Static)
            ?? throw new ReplException("the compiled cell has no entry point");
        var dependencies = session.Methods.Select(m => m.Trampoline.Definition).Concat(typeDependencies).Distinct().ToArray();
        var definition = SessionAssemblies.RegisterCell(assembly, created, dependencies, context);
        return new CompiledCell(assembly, created, method, state.Arguments.Select(a => a.Value).ToArray(), definition);
    }

    private static TypeBuilder DefineCellType(ModuleBuilder module) =>
        module.DefineType("IlRepl.Cell", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.Class | TypeAttributes.BeforeFieldInit);

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
