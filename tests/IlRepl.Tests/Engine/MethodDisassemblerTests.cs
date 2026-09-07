using System.Reflection;
using IlRepl.Engine;
using Mono.Cecil;
using Mono.Cecil.Cil;
using FieldDefinition = Mono.Cecil.FieldDefinition;
using MethodDefinition = Mono.Cecil.MethodDefinition;
using OpCodes = Mono.Cecil.Cil.OpCodes;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Tests for <see cref="MethodDisassembler"/> over session methods, Cecil-written bodies, and the framework.
/// </summary>
[TestClass]
public sealed class MethodDisassemblerTests
{
    private static readonly string[] Fib =
    [
        ".method int32 Fib(int32 n) {",
        "ldarg n",
        "ldc.i4 2",
        "blt BASE",
        "ldarg n",
        "ldc.i4 1",
        "sub",
        "call int32 Fib(int32)",
        "ldarg n",
        "ldc.i4 2",
        "sub",
        "call int32 Fib(int32)",
        "add",
        "ret",
        "BASE: ldarg n",
        "ret",
        "}",
    ];

    private static Session Session(params string[] lines)
    {
        var session = new Session();
        foreach (var line in lines)
        {
            session.AddLine(line);
        }

        return session;
    }

    private static DisassembledMethod Cecil(Action<ModuleDefinition, TypeDefinition> populate, string method = "M", Session? session = null)
    {
        session ??= new Session();
        var (_, _, fixture) = CecilFixture.Build(populate, session.Resolver);
        return MethodDisassembler.Disassemble(fixture.GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)!, session);
    }

    private static MethodDefinition Static(TypeDefinition type, string name, Mono.Cecil.TypeReference returnType)
    {
        var method = new MethodDefinition(name, Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Static, returnType);
        type.Methods.Add(method);
        return method;
    }

    /// <summary>
    /// A session method reads back with its bytes: arguments by slot, session calls by bare name, labels at branch targets.
    /// </summary>
    [TestMethod]
    public void Disassemble_SessionMethod_ReadsEmittedBody()
    {
        var session = Session(Fib);
        var fib = MethodDisassembler.Disassemble(session.Methods[0].Version.Body, session);
        Assert.AreEqual("image", fib.BodySource);
        Assert.StartsWith(".method public hidebysig static int32 Fib(int32 n) cil managed", fib.Header);
        var lines = DisassemblyText.LinesWithStack(fib);
        var text = string.Join("\n", lines);
        Assert.Contains("call int32 Fib(int32)\t[int32]", text);
        Assert.Contains("ldarg 0\t[int32]", text);
        Assert.Contains("\nIL_", text);
        Assert.Contains("blt IL_", text);
        var ret = fib.Entries.First(e => e.Instruction?.Op.Name == "ret");
        Assert.AreEqual(1, ret.Instruction!.RetPops);
        Assert.IsEmpty(fib.Problems);
        Assert.IsEmpty(fib.Notes, string.Join("; ", fib.Notes));
        Assert.AreEqual("[]", DisassemblyText.StackAt(fib, fib.Entries.Last(e => e.Instruction?.Op.Name == "ret").Offset));
    }

    /// <summary>
    /// A session method with a try and catch shows the leave the emitter added and boxes before ret.
    /// </summary>
    [TestMethod]
    public void Disassemble_SessionMethod_ShowsImplicitLeaveAndBox()
    {
        var session = Session(".method object Box() {", ".locals init (int32 v)", ".try {", "ldc.i4 1", "stloc v", "} catch [System.Runtime]System.Exception {", "pop", "ldc.i4 2", "stloc v", "}", "ldloc v", "box int32", "ret", "}");
        var box = MethodDisassembler.Disassemble(session.Methods[0].Version.Body, session);
        var lines = DisassemblyText.Lines(box);
        var text = string.Join("\n", lines);
        Assert.Contains(".try {", text);
        Assert.Contains("} catch Exception {", text);
        Assert.Contains("leave", text);
        Assert.Contains("box int32", text);
        Assert.AreSequenceEqual(["int32"], box.Locals.Select(IlSignatureRenderer.Pretty).ToList());
        Assert.IsTrue(box.InitLocals);
        var handler = box.Clauses.Single();
        Assert.AreEqual(IlClauseKind.Catch, handler.Kind);
        Assert.AreEqual(typeof(Exception), handler.CatchType);

        // The column shows the stack after each line: the handler's pop consumed the exception it was seeded with.
        Assert.AreEqual("[]", DisassemblyText.StackAt(box, handler.HandlerStart));
    }

    /// <summary>
    /// A committed class member lists with its own fields unqualified.
    /// </summary>
    [TestMethod]
    public void Disassemble_SessionTypeMember_ReadsRuntimeType()
    {
        var session = Session(".class public Point {", ".field public int32 X", ".method public instance int32 Get() {", "ldarg.0", "ldfld int32 Point::X", "ret", "}", "}");
        var resolved = MemberResolver.ResolveMethod("instance int32 Point::Get()", session.InspectionContext, false);
        var get = MethodDisassembler.Disassemble(resolved.Method!, session);
        var text = string.Join("\n", DisassemblyText.LinesWithStack(get));
        Assert.Contains("ldarg.0\t[Point]", text);
        Assert.Contains("ldfld int32 Point::X\t[int32]", text);
        Assert.StartsWith(".method public instance int32 Get() cil managed", get.Header);
    }

    /// <summary>
    /// Roslyn's catch plus finally pair (a finally over try and catch) folds into one region.
    /// </summary>
    [TestMethod]
    public void Disassemble_TryCatchFinally_FoldsIntoOneRegion()
    {
        var method = Cecil((module, type) =>
        {
            var m = Static(type, "M", module.TypeSystem.Int32);
            var result = new VariableDefinition(module.TypeSystem.Int32);
            m.Body.Variables.Add(result);
            var il = m.Body.GetILProcessor();
            var tryStart = il.Create(OpCodes.Ldc_I4_1);
            var catchStart = il.Create(OpCodes.Pop);
            var finallyStart = il.Create(OpCodes.Nop);
            var end = il.Create(OpCodes.Ldloc_0);
            il.Append(tryStart);
            il.Emit(OpCodes.Stloc_0);
            il.Emit(OpCodes.Leave, end);
            il.Append(catchStart);
            il.Emit(OpCodes.Ldc_I4_2);
            il.Emit(OpCodes.Stloc_0);
            il.Emit(OpCodes.Leave, end);
            il.Append(finallyStart);
            il.Emit(OpCodes.Endfinally);
            il.Append(end);
            il.Emit(OpCodes.Ret);
            m.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch) { TryStart = tryStart, TryEnd = catchStart, HandlerStart = catchStart, HandlerEnd = finallyStart, CatchType = module.ImportReference(typeof(Exception)) });
            m.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Finally) { TryStart = tryStart, TryEnd = finallyStart, HandlerStart = finallyStart, HandlerEnd = end });
        });
        var blocks = method.Entries.Where(e => e.Kind == DisassembledEntryKind.Block).Select(e => e.Block!.Value).ToList();
        Assert.AreSequenceEqual([BlockKind.Try, BlockKind.Catch, BlockKind.Finally, BlockKind.End], blocks);
        Assert.HasCount(2, method.Clauses);
        Assert.IsEmpty(method.Notes, string.Join("; ", method.Notes));
        var lines = DisassemblyText.LinesWithStack(method);
        Assert.Contains("} catch Exception {", lines);
        Assert.Contains("} finally {", lines);
        Assert.Contains(l => l.EndsWith("endfinally\t[]", StringComparison.Ordinal), lines);
        Assert.Contains(l => l.EndsWith("pop\t[]", StringComparison.Ordinal), lines);

        // The label for the leave target prints after the closing brace.
        var closing = lines.IndexOf("}");
        Assert.AreEqual("IL_", lines[closing + 1][..3]);
    }

    /// <summary>
    /// A filter clause starts at its filter offset; a fault clause is drawn too; both seed the stack.
    /// </summary>
    [TestMethod]
    public void Disassemble_FilterAndFault_UseFilterOffset()
    {
        var method = Cecil((module, type) =>
        {
            var m = Static(type, "M", module.TypeSystem.Void);
            var il = m.Body.GetILProcessor();
            var tryStart = il.Create(OpCodes.Nop);
            var filterStart = il.Create(OpCodes.Pop);
            var handlerStart = il.Create(OpCodes.Pop);
            var faultStart = il.Create(OpCodes.Nop);
            var end = il.Create(OpCodes.Ret);
            il.Append(tryStart);
            il.Emit(OpCodes.Leave, end);
            il.Append(filterStart);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Endfilter);
            il.Append(handlerStart);
            il.Emit(OpCodes.Leave, end);
            il.Append(faultStart);
            il.Emit(OpCodes.Endfinally);
            il.Append(end);
            m.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Filter) { TryStart = tryStart, TryEnd = filterStart, FilterStart = filterStart, HandlerStart = handlerStart, HandlerEnd = faultStart });
            m.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Fault) { TryStart = tryStart, TryEnd = faultStart, HandlerStart = faultStart, HandlerEnd = end });
        });
        var blocks = method.Entries.Where(e => e.Kind == DisassembledEntryKind.Block).Select(e => (e.Block!.Value, e.Offset)).ToList();
        var filter = method.Clauses.Single(c => c.Kind == IlClauseKind.Filter);
        Assert.AreSequenceEqual([(BlockKind.Try, 0), (BlockKind.Filter, filter.FilterStart!.Value), (BlockKind.FilterHandler, filter.HandlerStart), (BlockKind.Fault, filter.HandlerEnd), (BlockKind.End, method.CodeSize - 1)], blocks);
        Assert.IsEmpty(method.Notes, string.Join("; ", method.Notes));
        Assert.AreEqual("[]", DisassemblyText.StackAt(method, filter.FilterStart.Value));
        Assert.AreEqual("[]", DisassemblyText.StackAt(method, filter.HandlerStart));
        Assert.AreEqual("[int32]", DisassemblyText.StackAt(method, filter.FilterStart.Value + 1));
    }

    /// <summary>
    /// Clauses that braces cannot draw stay in offset form, with every boundary labeled and the handler still seeded.
    /// </summary>
    [TestMethod]
    public void Disassemble_NonContiguousClause_FallsBackToOffsets()
    {
        var method = Cecil((module, type) =>
        {
            var m = Static(type, "M", module.TypeSystem.Void);
            var il = m.Body.GetILProcessor();
            var tryStart = il.Create(OpCodes.Nop);
            var gap = il.Create(OpCodes.Nop);
            var handlerStart = il.Create(OpCodes.Pop);
            var end = il.Create(OpCodes.Ret);
            il.Append(tryStart);
            il.Emit(OpCodes.Leave, end);
            il.Append(gap);
            il.Emit(OpCodes.Br, end);
            il.Append(handlerStart);
            il.Emit(OpCodes.Leave, end);
            il.Append(end);
            m.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch) { TryStart = tryStart, TryEnd = gap, HandlerStart = handlerStart, HandlerEnd = end, CatchType = module.ImportReference(typeof(Exception)) });
        });
        Assert.DoesNotContain(e => e.Kind == DisassembledEntryKind.Block, method.Entries);
        Assert.HasCount(1, method.Clauses);
        var clause = method.Clauses[0];
        Assert.Contains(n => n.Contains(".try IL_0000 to IL_0006 catch [System.Runtime]System.Exception handler IL_", StringComparison.Ordinal), method.Notes);
        var labels = method.Entries.Where(e => e.Kind == DisassembledEntryKind.Label).Select(e => e.Offset).ToList();
        Assert.Contains(clause.TryEnd, labels);
        Assert.Contains(clause.HandlerStart, labels);

        // The handler was seeded with the exception even though no block was drawn: its pop leaves an empty, known stack.
        Assert.AreEqual("[]", DisassemblyText.StackAt(method, clause.HandlerStart));
    }

    /// <summary>
    /// A clause ending at the code size gets a label after the last instruction.
    /// </summary>
    [TestMethod]
    public void Disassemble_ClauseEndingAtCodeSize_LabelsTheEnd()
    {
        var method = Cecil((module, type) =>
        {
            var m = Static(type, "M", module.TypeSystem.Void);
            var il = m.Body.GetILProcessor();
            var tryStart = il.Create(OpCodes.Nop);
            var gap = il.Create(OpCodes.Nop);
            var handlerStart = il.Create(OpCodes.Nop);
            il.Append(tryStart);
            il.Emit(OpCodes.Ret);
            il.Append(gap);
            il.Emit(OpCodes.Ret);
            il.Append(handlerStart);
            il.Emit(OpCodes.Endfinally);
            m.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Finally) { TryStart = tryStart, TryEnd = gap, HandlerStart = handlerStart, HandlerEnd = null });
        });
        Assert.AreEqual(IlReader.LabelFor(method.CodeSize), method.Entries[^1].Label);
        Assert.AreEqual(DisassembledEntryKind.Label, method.Entries[^1].Kind);
    }

    /// <summary>
    /// Every literal operand prints in a form the parser reads back to the same bits.
    /// </summary>
    [TestMethod]
    public void Disassemble_LiteralOperands_RoundTrip()
    {
        var method = Cecil((module, type) =>
        {
            var m = Static(type, "M", module.TypeSystem.Void);
            var il = m.Body.GetILProcessor();
            il.Emit(OpCodes.Ldc_I4_S, (sbyte)-1);
            il.Emit(OpCodes.Ldc_I4, int.MinValue);
            il.Emit(OpCodes.Ldc_I8, long.MaxValue);
            il.Emit(OpCodes.Ldc_R4, 1.5f);
            il.Emit(OpCodes.Ldc_R8, -0.25);
            il.Emit(OpCodes.Ldc_R4, -0.0f);
            il.Emit(OpCodes.Ldc_R4, BitConverter.Int32BitsToSingle(0x7F800001));
            il.Emit(OpCodes.Ldc_R8, BitConverter.Int64BitsToDouble(unchecked((long)0xFFF8000000000123)));
            il.Emit(OpCodes.Ldc_R4, float.PositiveInfinity);
            il.Emit(OpCodes.Ldc_R8, double.NaN);
            il.Emit(OpCodes.Ldc_R4, float.Epsilon);
            il.Emit(OpCodes.Ldstr, "a\"b\n");
            il.Emit(OpCodes.Unaligned, (byte)2);
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Ldind_I4);
            il.Emit(OpCodes.Ret);
        });
        var texts = DisassemblyText.Instructions(method);
        Assert.AreSequenceEqual(
            ["ldc.i4.s -1", "ldc.i4 -2147483648", "ldc.i8 9223372036854775807", "ldc.r4 1.5", "ldc.r8 -0.25", "ldc.r4 -0.0", "ldc.r4 float32(0x7f800001)", "ldc.r8 float64(0xfff8000000000123)", "ldc.r4 float32(0x7f800000)", "ldc.r8 float64(0xfff8000000000000)", "ldc.r4 1E-45", "ldstr \"a\\\"b\\n\"", "unaligned. 2", "ldnull", "ldind.i4", "ret"],
            texts);
        foreach (var entry in method.Entries.Where(e => e.Instruction?.Kind is OperandKind.Single or OperandKind.Double))
        {
            var parsed = InstructionParser.Parse(entry.Instruction!.Text, method.Context);
            if (entry.Instruction.Kind == OperandKind.Single)
            {
                Assert.AreEqual(BitConverter.SingleToInt32Bits((float)entry.Instruction.Operand!), BitConverter.SingleToInt32Bits((float)parsed.Operand!), entry.Instruction.Text);
            }
            else
            {
                Assert.AreEqual(BitConverter.DoubleToInt64Bits((double)entry.Instruction.Operand!), BitConverter.DoubleToInt64Bits((double)parsed.Operand!), entry.Instruction.Text);
            }
        }
    }

    /// <summary>
    /// Switch, prefixes, the no. prefix, jmp, and arglist all print as their bytes say.
    /// </summary>
    [TestMethod]
    public void Disassemble_SwitchPrefixesAndRareOpcodes_Print()
    {
        var method = Cecil((module, type) =>
        {
            var target = Static(type, "Target", module.TypeSystem.Void);
            target.Body.GetILProcessor().Emit(OpCodes.Ret);
            var m = Static(type, "M", module.TypeSystem.Void);
            m.Parameters.Add(new ParameterDefinition("x", Mono.Cecil.ParameterAttributes.None, module.TypeSystem.Int32));
            var il = m.Body.GetILProcessor();
            var a = il.Create(OpCodes.Nop);
            var b = il.Create(OpCodes.Nop);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Switch, new[] { a, b });
            il.Append(a);
            il.Append(b);
            il.Emit(OpCodes.Ldarga_S, m.Parameters[0]);
            il.Emit(OpCodes.Volatile);
            il.Emit(OpCodes.Ldind_I4);
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Tail);
            il.Emit(OpCodes.Call, target);
            il.Emit(OpCodes.Ret);
            il.Emit(OpCodes.Jmp, target);
        });
        var texts = DisassemblyText.Instructions(method);
        Assert.AreSequenceEqual(["ldarg.0", "switch (IL_000e, IL_000f)", "nop", "nop", "ldarga.s 0", "volatile.", "ldind.i4", "pop", "tail.", "call void [" + method.Method.Module.Assembly.GetName().Name + "]N.Fixture::Target()", "ret", "jmp void [" + method.Method.Module.Assembly.GetName().Name + "]N.Fixture::Target()"], texts);
        var column = DisassemblyText.LinesWithStack(method);
        Assert.Contains(l => l.EndsWith("switch (IL_000e, IL_000f)\t[]", StringComparison.Ordinal), column);
        Assert.Contains(l => l.EndsWith("jmp void [" + method.Method.Module.Assembly.GetName().Name + "]N.Fixture::Target()\tunreachable", StringComparison.Ordinal), column);

        // no. and arglist come from raw bytes Cecil does not write.
        var vararg = Cecil((module, type) =>
        {
            var m = new MethodDefinition("V", Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Static, module.TypeSystem.Void) { CallingConvention = MethodCallingConvention.VarArg };
            var il = m.Body.GetILProcessor();
            il.Emit(OpCodes.Arglist);
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ret);
            type.Methods.Add(m);
        }, "V");
        var lines = DisassemblyText.LinesWithStack(vararg);
        Assert.Contains(l => l.EndsWith("arglist\t[RuntimeArgumentHandle]", StringComparison.Ordinal), lines);
        Assert.Contains("vararg", vararg.Header);
    }

    /// <summary>
    /// Members of generic types and generic methods resolve through their definitions, with positional parameters.
    /// </summary>
    [TestMethod]
    public void Disassemble_GenericDefinitions_ResolveTokens()
    {
        var session = new Session();
        var add = MethodDisassembler.Disassemble(typeof(List<int>).GetMethod("Add")!, session);
        Assert.Contains(n => n.StartsWith("showing the definition", StringComparison.Ordinal), add.Notes);
        Assert.IsTrue(add.Method.DeclaringType!.IsGenericTypeDefinition);
        Assert.DoesNotContain(e => e.Kind == DisassembledEntryKind.Raw, add.Entries, string.Join("\n", DisassemblyText.Lines(add)));
        Assert.Contains("!0", string.Join("\n", DisassemblyText.Lines(add)));
        Assert.StartsWith(".method public hidebysig newslot virtual final instance void Add(!T item) cil managed", add.Header);

        var first = MethodDisassembler.Disassemble(typeof(Enumerable).GetMethod("First", [typeof(IEnumerable<>).MakeGenericType(Type.MakeGenericMethodParameter(0))])!.MakeGenericMethod(typeof(int)), session);
        Assert.Contains(n => n.StartsWith("showing the definition", StringComparison.Ordinal), first.Notes);
        Assert.IsTrue(first.Method.IsGenericMethodDefinition);
        Assert.Contains("<TSource>", first.Header);
        Assert.DoesNotContain(e => e.Kind == DisassembledEntryKind.Raw, first.Entries, string.Join("\n", DisassemblyText.Lines(first)));
    }

    /// <summary>
    /// ldtoken prints type, field, and method forms and pushes the matching handle.
    /// </summary>
    [TestMethod]
    public void Disassemble_Ldtoken_PrintsThreeForms()
    {
        var method = Cecil((module, type) =>
        {
            type.Fields.Add(new FieldDefinition("F", Mono.Cecil.FieldAttributes.Public | Mono.Cecil.FieldAttributes.Static, module.TypeSystem.Int32));
            var m = Static(type, "M", module.TypeSystem.Void);
            var il = m.Body.GetILProcessor();
            il.Emit(OpCodes.Ldtoken, module.TypeSystem.Int32);
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ldtoken, type.Fields[0]);
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ldtoken, m);
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ret);
        });
        var assembly = method.Method.Module.Assembly.GetName().Name;
        var lines = DisassemblyText.LinesWithStack(method);
        Assert.Contains("0000 ldtoken int32\t[RuntimeTypeHandle]", lines);
        Assert.Contains($"0006 ldtoken field int32 [{assembly}]N.Fixture::F\t[RuntimeFieldHandle]", lines);
        Assert.Contains($"000c ldtoken method void [{assembly}]N.Fixture::M()\t[RuntimeMethodHandle]", lines);
    }

    /// <summary>
    /// A calli operand prints its signature and pops what it declares; an explicit this counts once.
    /// </summary>
    [TestMethod]
    public void Disassemble_Calli_PrintsSignatureAndPops()
    {
        var method = Cecil((module, type) =>
        {
            var m = Static(type, "M", module.TypeSystem.Int32);
            var il = m.Body.GetILProcessor();
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Conv_I);
            var site = new CallSite(module.TypeSystem.Int32) { HasThis = true, ExplicitThis = true };
            site.Parameters.Add(new ParameterDefinition(module.TypeSystem.Object));
            il.Emit(OpCodes.Calli, site);
            il.Emit(OpCodes.Ret);
        });
        var lines = DisassemblyText.LinesWithStack(method);
        Assert.Contains(l => l.EndsWith("calli instance explicit int32(object)\t[int32]", StringComparison.Ordinal), lines);
    }

    /// <summary>
    /// A vararg call site prints the optional argument types its MemberRef carries and pops them.
    /// </summary>
    [TestMethod]
    public void Disassemble_VarargCallSite_ReadsOptionalTypesFromMetadata()
    {
        var method = Cecil((module, type) =>
        {
            var callee = new MethodDefinition("Count", Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Static, module.TypeSystem.Int32) { CallingConvention = MethodCallingConvention.VarArg };
            callee.Body.GetILProcessor().Emit(OpCodes.Ldc_I4_0);
            callee.Body.GetILProcessor().Emit(OpCodes.Ret);
            type.Methods.Add(callee);
            var m = Static(type, "M", module.TypeSystem.Int32);
            var site = new MethodReference("Count", module.TypeSystem.Int32, type) { CallingConvention = MethodCallingConvention.VarArg };
            site.Parameters.Add(new ParameterDefinition(new SentinelType(module.TypeSystem.Int32)));
            var il = m.Body.GetILProcessor();
            il.Emit(OpCodes.Ldc_I4, 123);
            il.Emit(OpCodes.Call, site);
            il.Emit(OpCodes.Ret);
        });
        var assembly = method.Method.Module.Assembly.GetName().Name;
        var lines = DisassemblyText.LinesWithStack(method);
        Assert.Contains($"0005 call vararg int32 [{assembly}]N.Fixture::Count(..., int32)\t[int32]", lines);
        Assert.IsEmpty(method.Notes, string.Join("; ", method.Notes));
    }

    /// <summary>
    /// A member reference that does not resolve prints what its row says and leaves the stack unknown from there.
    /// </summary>
    [TestMethod]
    public void Disassemble_MissingMember_PrintsRowAndUnknownStack()
    {
        var method = Cecil((module, type) =>
        {
            var m = Static(type, "M", module.TypeSystem.Void);
            var missing = new MethodReference("Missing_Review_Test", module.TypeSystem.Void, module.ImportReference(typeof(string)));
            var missingField = new FieldReference("Gone", module.TypeSystem.Int32, module.ImportReference(typeof(string)));
            var il = m.Body.GetILProcessor();
            il.Emit(OpCodes.Call, missing);
            il.Emit(OpCodes.Ldsfld, missingField);
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ret);
        });
        var lines = DisassemblyText.LinesWithStack(method);
        Assert.AreEqual("0000 call void string::Missing_Review_Test()\t?", lines[0]);
        Assert.AreEqual("0005 ldsfld int32 string::Gone\t?", lines[1]);
        Assert.AreEqual(DisassembledEntryKind.Raw, method.Entries[0].Kind);
        Assert.Contains(n => n.Contains("the method at IL_0000 could not be resolved", StringComparison.Ordinal), method.Notes);
        Assert.Contains(n => n.Contains("the field at IL_0005 could not be resolved", StringComparison.Ordinal), method.Notes);
        Assert.EndsWith("ret\t?", lines[^1]);
    }

    /// <summary>
    /// A body whose unused local names a missing assembly still lists, from the image.
    /// </summary>
    [TestMethod]
    public void Disassemble_LocalOfMissingType_ListsFromImage()
    {
        var method = Cecil((module, type) =>
        {
            var scope = new AssemblyNameReference("MissingReviewDependency", new Version(1, 0, 0, 0));
            module.AssemblyReferences.Add(scope);
            var missing = new Mono.Cecil.TypeReference("Missing", "Thing", module, scope);
            var m = Static(type, "M", module.TypeSystem.Void);
            m.Body.Variables.Add(new VariableDefinition(missing));
            m.Body.GetILProcessor().Emit(OpCodes.Ret);
        });
        Assert.Throws<FileNotFoundException>(() => method.Method.GetMethodBody());
        Assert.AreEqual("image", method.BodySource);
        Assert.AreSequenceEqual(["ret"], DisassemblyText.Instructions(method));
        Assert.AreEqual("class [MissingReviewDependency]Missing.Thing", IlSignatureRenderer.IlAsm(method.Locals[0]));
        Assert.AreEqual("[]", DisassemblyText.StackAt(method, 0));
    }

    /// <summary>
    /// A local or argument index past what is declared is a raw line with a note, never a crash.
    /// </summary>
    [TestMethod]
    public void Disassemble_IndexPastDeclarations_IsRawLine()
    {
        var patched = Cecil((module, type) =>
        {
            var m = Static(type, "M", module.TypeSystem.Void);
            var il = m.Body.GetILProcessor();
            il.Emit(OpCodes.Ldloc_3);
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ret);
        });
        var lines = DisassemblyText.LinesWithStack(patched);
        Assert.AreEqual("0000 ldloc.3\t?", lines[0]);
        Assert.AreEqual("0002 ldarg.1\t?", lines[2]);
        Assert.Contains(n => n.Contains("names local 3 but the body declares 0", StringComparison.Ordinal), patched.Notes);
        Assert.Contains(n => n.Contains("names argument 1 but the method has 0", StringComparison.Ordinal), patched.Notes);
    }

    /// <summary>
    /// Methods without IL are refused with a message that says why.
    /// </summary>
    [TestMethod]
    public void Disassemble_NoBody_ExplainsWhy()
    {
        var session = new Session();
        var (_, _, fixture) = CecilFixture.Build((module, type) =>
        {
            type.Attributes |= Mono.Cecil.TypeAttributes.Abstract;
            type.Methods.Add(new MethodDefinition("Abstract", Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Abstract | Mono.Cecil.MethodAttributes.Virtual | Mono.Cecil.MethodAttributes.NewSlot, module.TypeSystem.Void));
            type.Methods.Add(new MethodDefinition("Internal", Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Static, module.TypeSystem.Void) { ImplAttributes = Mono.Cecil.MethodImplAttributes.InternalCall });
            module.ModuleReferences.Add(new ModuleReference("libc"));
            var native = new MethodDefinition("Native", Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Static | Mono.Cecil.MethodAttributes.PInvokeImpl, module.TypeSystem.Int32)
            {
                PInvokeInfo = new PInvokeInfo(PInvokeAttributes.CallConvCdecl, "getpid", module.ModuleReferences[0]),
                ImplAttributes = Mono.Cecil.MethodImplAttributes.PreserveSig,
            };
            type.Methods.Add(native);
        });
        Assert.Contains("is abstract", Assert.Throws<ReplException>(() => MethodDisassembler.Disassemble(fixture.GetMethod("Abstract")!, session)).Message);
        Assert.Contains("implemented by the runtime", Assert.Throws<ReplException>(() => MethodDisassembler.Disassemble(fixture.GetMethod("Internal")!, session)).Message);
        Assert.Contains("implemented by the runtime", Assert.Throws<ReplException>(() => MethodDisassembler.Disassemble(fixture.GetMethod("Native")!, session)).Message);
        Assert.Contains("is abstract", Assert.Throws<ReplException>(() => MethodDisassembler.Disassemble(typeof(Stream).GetMethod("Flush")!, session)).Message);
        var dynamic = new System.Reflection.Emit.DynamicMethod("D", typeof(void), Type.EmptyTypes);
        dynamic.GetILGenerator().Emit(System.Reflection.Emit.OpCodes.Ret);
        Assert.Contains("dynamic method", Assert.Throws<ReplException>(() => MethodDisassembler.Disassemble(dynamic, session)).Message);
    }

    /// <summary>
    /// Inspecting a type runs no initializer: a counter on another class the .cctor would bump stays at zero.
    /// </summary>
    [TestMethod]
    public void Disassemble_RunsNoInitializer()
    {
        var session = Session(
            ".class public Witness {", ".field public static int32 Count", "}",
            ".class public Lazy {", ".field public static int32 Value",
            ".method public static specialname rtspecialname void .cctor() {", "ldsfld int32 Witness::Count", "ldc.i4 1", "add", "stsfld int32 Witness::Count", "ldc.i4 42", "stsfld int32 Lazy::Value", "ret", "}",
            ".method public static int32 Get() {", "ldsfld int32 Lazy::Value", "ret", "}", "}");
        var witness = session.TypeTable.TryResolve("Witness", false, false, out var w) ? w! : throw new AssertFailedException("Witness");
        var get = MemberResolver.ResolveMethod("int32 Lazy::Get()", session.InspectionContext, false).Method!;
        var listing = MethodDisassembler.Disassemble(get, session);
        _ = StackAnalysis.Run(listing);
        Assert.Contains("ldsfld int32 Lazy::Value", string.Join("\n", DisassemblyText.Lines(listing)));
        Assert.AreEqual(0, witness.GetField("Count")!.GetValue(null));
    }

    /// <summary>
    /// A loaded assembly lists from the bytes read at load time, even after the file changes.
    /// </summary>
    [TestMethod]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows, IgnoreMessage = "Windows keeps a loaded assembly's file locked, so it cannot be replaced while loaded; the image path is exercised on Unix")]
    public void Disassemble_LoadedAssembly_UsesImageReadAtLoad()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ilrepl-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "Replaced.dll");
        try
        {
            static byte[] Write(int value)
            {
                using var definition = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("Replaced", new Version(1, 0, 0, 0)), "Replaced", ModuleKind.Dll);
                var module = definition.MainModule;
                var type = new TypeDefinition("N", "R", Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class, module.TypeSystem.Object);
                module.Types.Add(type);
                var m = new MethodDefinition("Value", Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Static, module.TypeSystem.Int32);
                m.Body.GetILProcessor().Emit(OpCodes.Ldc_I4, value);
                m.Body.GetILProcessor().Emit(OpCodes.Ret);
                type.Methods.Add(m);
                using var stream = new MemoryStream();
                definition.Write(stream);
                return stream.ToArray();
            }

            File.WriteAllBytes(path, Write(1));
            var session = new Session();
            var assembly = session.Resolver.Load(path);
            var replacement = Path.Combine(directory, "Replaced.new");
            File.WriteAllBytes(replacement, Write(2));
            File.Move(replacement, path, overwrite: true);
            var listing = MethodDisassembler.Disassemble(assembly.GetType("N.R")!.GetMethod("Value")!, session);
            Assert.AreEqual("image", listing.BodySource);
            Assert.AreSequenceEqual(["ldc.i4 1", "ret"], DisassemblyText.Instructions(listing));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Handlers that share a try keep their metadata order, which is the dispatch order; when
    /// that order is not their lexical order, braces cannot draw them and the offset form does.
    /// </summary>
    [TestMethod]
    public void Disassemble_HandlersOutOfLexicalOrder_KeepMetadataOrder()
    {
        var method = Cecil((module, type) => AddOutOfOrderHandlers(module, type));
        Assert.DoesNotContain(e => e.Kind == DisassembledEntryKind.Block, method.Entries);
        Assert.AreEqual(IlClauseKind.Catch, method.Clauses[0].Kind);
        Assert.AreEqual(typeof(Exception), method.Clauses[0].CatchType, "the first clause in metadata is the Exception handler");
        Assert.AreEqual(typeof(ArgumentException), method.Clauses[1].CatchType);
        var notes = method.Notes.Where(n => n.StartsWith("clause not drawn", StringComparison.Ordinal)).ToList();
        Assert.HasCount(2, notes);
        Assert.Contains("catch [System.Runtime]System.Exception", notes[0]);
        Assert.Contains("catch [System.Runtime]System.ArgumentException", notes[1]);
        var native = IlAsmClauseWriter.Write(method);
        Assert.Contains(".try IL_0000 to IL_", native);
        Assert.IsLessThan(native.IndexOf("System.ArgumentException handler", StringComparison.Ordinal), native.IndexOf("System.Exception handler", StringComparison.Ordinal), native);
    }

    /// <summary>
    /// Writes a body whose two catch handlers sit in the opposite order from their clauses:
    /// the Exception handler comes first in metadata but last in the bytes, so it must win.
    /// </summary>
    internal static void AddOutOfOrderHandlers(ModuleDefinition module, TypeDefinition type)
    {
        var m = Static(type, "M", module.TypeSystem.Int32);
        m.Body.Variables.Add(new VariableDefinition(module.TypeSystem.Int32));
        var il = m.Body.GetILProcessor();
        var tryStart = il.Create(OpCodes.Ldstr, "boom");
        var argumentHandler = il.Create(OpCodes.Pop);
        var exceptionHandler = il.Create(OpCodes.Pop);
        var end = il.Create(OpCodes.Ldloc_0);
        il.Append(tryStart);
        il.Emit(OpCodes.Newobj, module.ImportReference(typeof(ArgumentException).GetConstructor([typeof(string)])!));
        il.Emit(OpCodes.Throw);
        il.Append(argumentHandler);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Stloc_0);
        il.Emit(OpCodes.Leave, end);
        il.Append(exceptionHandler);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Stloc_0);
        il.Emit(OpCodes.Leave, end);
        il.Append(end);
        il.Emit(OpCodes.Ret);
        m.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch) { TryStart = tryStart, TryEnd = argumentHandler, HandlerStart = exceptionHandler, HandlerEnd = end, CatchType = module.ImportReference(typeof(Exception)) });
        m.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch) { TryStart = tryStart, TryEnd = argumentHandler, HandlerStart = argumentHandler, HandlerEnd = exceptionHandler, CatchType = module.ImportReference(typeof(ArgumentException)) });
    }

    /// <summary>
    /// A token for an open generic type prints the definition, not an instantiation over its own parameters.
    /// </summary>
    [TestMethod]
    public void Disassemble_OpenGenericTypeToken_PrintsDefinition()
    {
        var method = Cecil((module, type) =>
        {
            var m = Static(type, "M", module.TypeSystem.Void);
            var il = m.Body.GetILProcessor();
            il.Emit(OpCodes.Ldtoken, module.ImportReference(typeof(List<>)));
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ret);
        });
        var text = method.Entries[0].Instruction!.Text;
        // Bare, without the class keyword: that is the spelling ilasm turns into a TypeRef, as the C# compiler writes it.
        Assert.AreEqual("ldtoken [System.Collections]System.Collections.Generic.List`1", text);
        var parsed = InstructionParser.Parse(text, method.Context);
        Assert.AreEqual(typeof(List<>), parsed.Operand);
    }

    /// <summary>
    /// A token for a generic method definition keeps its arity, so it does not read back as a non-generic overload.
    /// </summary>
    [TestMethod]
    public void Disassemble_GenericMethodDefinitionToken_KeepsArity()
    {
        var session = new Session();
        var method = Cecil((module, type) =>
        {
            var generic = new MethodDefinition("Generic", Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Static, module.TypeSystem.Void);
            var t = new Mono.Cecil.GenericParameter("T", generic);
            generic.GenericParameters.Add(t);
            generic.ReturnType = t;
            generic.Body.GetILProcessor().Emit(OpCodes.Ldnull);
            generic.Body.GetILProcessor().Emit(OpCodes.Ret);
            type.Methods.Add(generic);
            var plain = Static(type, "Generic", module.TypeSystem.Int32);
            plain.Body.GetILProcessor().Emit(OpCodes.Ldc_I4_0);
            plain.Body.GetILProcessor().Emit(OpCodes.Ret);
            var m = Static(type, "M", module.TypeSystem.Void);
            var il = m.Body.GetILProcessor();
            il.Emit(OpCodes.Ldtoken, generic);
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ldtoken, plain);
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ret);
        }, session: session);
        var assembly = method.Method.Module.Assembly.GetName().Name;
        var texts = DisassemblyText.Instructions(method);
        Assert.AreEqual($"ldtoken method !!0 [{assembly}]N.Fixture::Generic<[1]>()", texts[0]);
        Assert.AreEqual($"ldtoken method int32 [{assembly}]N.Fixture::Generic()", texts[2]);
        var definition = (ResolvedMethod)InstructionParser.Parse(texts[0], method.Context).Operand!;
        Assert.IsTrue(definition.Method!.IsGenericMethodDefinition);
        var plainMethod = (ResolvedMethod)InstructionParser.Parse(texts[2], method.Context).Operand!;
        Assert.IsFalse(plainMethod.Method!.IsGenericMethod);
    }

    /// <summary>
    /// Members of an open generic type print their owner as the definition, with no invented instantiation.
    /// </summary>
    [TestMethod]
    public void Disassemble_MembersOfOpenGenericOwner_PrintTheDefinition()
    {
        var method = Cecil((module, type) =>
        {
            var owner = new TypeDefinition("N", "GenericOwner`1", Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class, module.TypeSystem.Object);
            module.Types.Add(owner);
            var t = new Mono.Cecil.GenericParameter("T", owner);
            owner.GenericParameters.Add(t);
            var field = new FieldDefinition("Value", Mono.Cecil.FieldAttributes.Public | Mono.Cecil.FieldAttributes.Static, t);
            owner.Fields.Add(field);
            var identity = Static(owner, "Identity", module.TypeSystem.Void);
            var u = new Mono.Cecil.GenericParameter("U", identity);
            identity.GenericParameters.Add(u);
            identity.ReturnType = u;
            identity.Parameters.Add(new ParameterDefinition(u));
            identity.Body.GetILProcessor().Emit(OpCodes.Ldarg_0);
            identity.Body.GetILProcessor().Emit(OpCodes.Ret);
            var m = Static(type, "M", module.TypeSystem.Void);
            var il = m.Body.GetILProcessor();
            il.Emit(OpCodes.Ldtoken, identity);
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ldtoken, field);
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ret);
        });
        var assembly = method.Method.Module.Assembly.GetName().Name;
        var texts = DisassemblyText.Instructions(method);
        Assert.AreEqual($"ldtoken method !!0 [{assembly}]N.GenericOwner`1::Identity<[1]>(!!0)", texts[0]);
        Assert.AreEqual($"ldtoken field !0 [{assembly}]N.GenericOwner`1::Value", texts[2]);
        var identityToken = (ResolvedMethod)InstructionParser.Parse(texts[0], method.Context).Operand!;
        Assert.IsTrue(identityToken.Method!.IsGenericMethodDefinition);
        Assert.IsTrue(identityToken.Method.DeclaringType!.IsGenericTypeDefinition);
        var fieldToken = (System.Reflection.FieldInfo)InstructionParser.Parse(texts[2], method.Context).Operand!;
        Assert.AreEqual("Value", fieldToken.Name);
    }

    /// <summary>
    /// A type whose name holds a delimiter prints quoted and parses back, whatever the delimiter.
    /// </summary>
    [TestMethod]
    public void Disassemble_QuotedNamesWithDelimiters_ParseBack()
    {
        var method = Cecil((module, type) =>
        {
            var paren = new TypeDefinition("N", "Paren(Name", Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class, module.TypeSystem.Object);
            var comma = new TypeDefinition("N", "Comma,Name<T", Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class, module.TypeSystem.Object);
            module.Types.Add(paren);
            module.Types.Add(comma);
            var use = Static(type, "Use", module.TypeSystem.Void);
            use.Parameters.Add(new ParameterDefinition(paren));
            use.Parameters.Add(new ParameterDefinition(comma));
            use.Body.GetILProcessor().Emit(OpCodes.Ret);
            var m = Static(type, "M", module.TypeSystem.Void);
            var il = m.Body.GetILProcessor();
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Call, use);
            il.Emit(OpCodes.Ret);
        });
        var assembly = method.Method.Module.Assembly.GetName().Name;
        var call = DisassemblyText.Instructions(method)[2];
        Assert.AreEqual($"call void [{assembly}]N.Fixture::Use(class [{assembly}]N.'Paren(Name', class [{assembly}]N.'Comma,Name<T')", call);
        var parsed = (ResolvedMethod)InstructionParser.Parse(call, method.Context).Operand!;
        Assert.AreEqual("Use", parsed.Method!.Name);
        Assert.AreEqual("[]", DisassemblyText.StackAt(method, method.Entries[2].Offset));
    }

    /// <summary>
    /// A damaged field, method, or method instance token keeps its value on the line.
    /// </summary>
    [TestMethod]
    public void Disassemble_DamagedDefinitionTokens_KeepTheirValue()
    {
        var session = new Session();
        var (_, image, _) = CecilFixture.Build((module, type) =>
        {
            type.Fields.Add(new FieldDefinition("F", Mono.Cecil.FieldAttributes.Public | Mono.Cecil.FieldAttributes.Static, module.TypeSystem.Int32));
            var generic = Static(type, "G", module.TypeSystem.Void);
            generic.GenericParameters.Add(new Mono.Cecil.GenericParameter("T", generic));
            generic.Body.GetILProcessor().Emit(OpCodes.Ret);
            var plain = Static(type, "P", module.TypeSystem.Void);
            plain.Body.GetILProcessor().Emit(OpCodes.Ret);
            var instance = new GenericInstanceMethod(generic);
            instance.GenericArguments.Add(module.TypeSystem.Int32);
            var m = Static(type, "M", module.TypeSystem.Void);
            var il = m.Body.GetILProcessor();
            il.Emit(OpCodes.Ldtoken, type.Fields[0]);
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ldtoken, plain);
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ldtoken, instance);
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ret);
        });

        var patched = (byte[])image.Clone();
        var found = 0;
        for (var i = 0; i + 5 < patched.Length; i++)
        {
            if (patched[i] == 0xD0 && patched[i + 4] is 0x04 or 0x06 or 0x2B && patched[i + 5] == 0x26)
            {
                patched[i + 1] = 0xFF;
                patched[i + 2] = 0xFF;
                patched[i + 3] = 0xFF;
                found++;
            }
        }

        Assert.AreEqual(3, found, "three ldtoken operands should be in the image");
        var assembly = session.Resolver.LoadImage(patched);
        var listing = MethodDisassembler.Disassemble(assembly.GetType("N.Fixture")!.GetMethod("M")!, session);
        Assert.AreSequenceEqual(["ldtoken 0x04ffffff", "pop", "ldtoken 0x06ffffff", "pop", "ldtoken 0x2bffffff", "pop", "ret"], DisassemblyText.Instructions(listing));
    }

    /// <summary>
    /// A damaged token names no row: the line prints the token, a note says so, and the rest of the body lists.
    /// </summary>
    [TestMethod]
    public void Disassemble_DamagedToken_IsRawLineWithNote()
    {
        var session = new Session();
        var (_, image, _) = CecilFixture.Build((module, type) =>
        {
            var m = Static(type, "M", module.TypeSystem.Void);
            var il = m.Body.GetILProcessor();
            il.Emit(OpCodes.Ldtoken, module.ImportReference(typeof(string).GetMethod("Trim", Type.EmptyTypes)!));
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ret);
        });

        // The ldtoken operand is a MemberRef; point it at a row the table does not have.
        var patched = (byte[])image.Clone();
        var at = -1;
        for (var i = 0; i + 5 < patched.Length; i++)
        {
            if (patched[i] == 0xD0 && patched[i + 4] == 0x0A && patched[i + 5] == 0x26)
            {
                at = i;
                break;
            }
        }

        Assert.IsGreaterThan(0, at, "the ldtoken bytes should be in the image");
        patched[at + 1] = 0xFF;
        patched[at + 2] = 0xFF;
        patched[at + 3] = 0xFF;
        var assembly = session.Resolver.LoadImage(patched);
        var listing = MethodDisassembler.Disassemble(assembly.GetType("N.Fixture")!.GetMethod("M")!, session);
        var lines = DisassemblyText.LinesWithStack(listing);
        Assert.AreEqual("0000 ldtoken 0x0affffff\t?", lines[0]);
        Assert.AreEqual(DisassembledEntryKind.Raw, listing.Entries[0].Kind);
        Assert.Contains(n => n.Contains("IL_0000", StringComparison.Ordinal), listing.Notes);
        Assert.AreSequenceEqual(["ldtoken 0x0affffff", "pop", "ret"], DisassemblyText.Instructions(listing));
    }

    /// <summary>
    /// The Greeter sample lists after .load, its vararg caller included, and the loaded type is not rooted by the listing.
    /// </summary>
    [TestMethod]
    public void Disassemble_Greeter_ListsVarargCaller()
    {
        var session = new Session();
        session.Resolver.Load(SampleHost.Samples.GreeterDll);
        var caller = MemberResolver.ResolveMethod("int32 Greeter.Hello::CallCountArgs()", session.InspectionContext, false).Method!;
        var listing = MethodDisassembler.Disassemble(caller, session);
        var lines = DisassemblyText.LinesWithStack(listing);
        Assert.Contains(l => l.EndsWith("call vararg int32 [Greeter]Greeter.Hello::CountArgs(..., int32)\t[int32]", StringComparison.Ordinal), lines, string.Join("\n", lines));
        Assert.IsEmpty(listing.Notes, string.Join("; ", listing.Notes));
        var say = MemberResolver.ResolveMethod("string Greeter.Hello::Say(string)", session.InspectionContext, false).Method!;
        var sayListing = MethodDisassembler.Disassemble(say, session);
        Assert.Contains("call string string::Concat(string, string, string)", string.Join("\n", DisassemblyText.Lines(sayListing)));
    }

    /// <summary>
    /// After .reset a definition that was listed is still collected.
    /// </summary>
    [TestMethod]
    public void Disassemble_KeepsNoReference_DefinitionCollectsAfterReset()
    {
        var session = new Session();
        var reference = ListAndReset(session);
        for (var i = 0; i < 5 && reference.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        Assert.IsFalse(reference.IsAlive, "the listed definition should be collectable after .reset");
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference ListAndReset(Session session)
    {
        foreach (var line in new[] { ".method int32 Two() {", "ldc.i4 2", "ret", "}" })
        {
            session.AddLine(line);
        }

        var body = session.Methods[0].Version.Body;
        var listing = MethodDisassembler.Disassemble(body, session);
        _ = StackAnalysis.Run(listing);
        var reference = new WeakReference(body.Module.Assembly);
        session.Reset();
        return reference;
    }

    /// <summary>
    /// Writes two types whose names differ by one literal backslash, each with a Value method
    /// returning 1 and 2, and a method M that calls the first.
    /// </summary>
    internal static void AddBackslashTypes(ModuleDefinition module, TypeDefinition type)
    {
        foreach (var (name, value) in new[] { ("Slash\\Name", 1), ("SlashName", 2) })
        {
            var owner = new TypeDefinition("N", name, Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class, module.TypeSystem.Object);
            module.Types.Add(owner);
            var valueMethod = Static(owner, "Value", module.TypeSystem.Int32);
            valueMethod.Body.GetILProcessor().Emit(OpCodes.Ldc_I4, value);
            valueMethod.Body.GetILProcessor().Emit(OpCodes.Ret);
        }

        var m = Static(type, "M", module.TypeSystem.Int32);
        var il = m.Body.GetILProcessor();
        il.Emit(OpCodes.Call, module.Types.First(t => t.Name == "Slash\\Name").Methods[0]);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// A literal backslash in a type name survives reflection's escaping, ILAsm quoting, and the parser, so the listing names the same type.
    /// </summary>
    [TestMethod]
    public void Disassemble_BackslashInTypeName_KeepsIdentity()
    {
        var method = Cecil(AddBackslashTypes);
        var assembly = method.Method.Module.Assembly.GetName().Name;
        var call = DisassemblyText.Instructions(method)[0];
        Assert.AreEqual($"call int32 [{assembly}]N.'Slash\\\\Name'::Value()", call);
        var parsed = (ResolvedMethod)InstructionParser.Parse(call, method.Context).Operand!;

        // Reflection spells the name with its own escaping, so the metadata name is checked through Cecil and the value.
        Assert.AreEqual("Slash\\\\Name", parsed.Method!.DeclaringType!.Name);
        Assert.AreEqual(1, parsed.Method.Invoke(null, null));
        Assert.AreEqual(1, method.Method.Invoke(null, null));
    }

    /// <summary>
    /// A core type that no facade exports is spelled with its defining assembly, which is the only reference that binds.
    /// </summary>
    [TestMethod]
    public void Disassemble_UnexportedCoreType_NamesTheDefiningAssembly()
    {
        var marvin = typeof(string).Assembly.GetType("System.Marvin")!;
        Assert.AreEqual("System.Private.CoreLib", TypeNameFormatter.AssemblyReferenceName(marvin));
        var session = new Session();
        var hash = MethodDisassembler.Disassemble(typeof(string).GetMethod("GetHashCode", Type.EmptyTypes)!, session);
        var texts = DisassemblyText.Instructions(hash);
        var marvinCall = texts.First(t => t.Contains("System.Marvin::", StringComparison.Ordinal));
        Assert.Contains("[System.Private.CoreLib]System.Marvin::", marvinCall);
        Assert.DoesNotContain(t => t.Contains("[System.Runtime]System.Marvin", StringComparison.Ordinal), texts);
        var parsed = (ResolvedMethod)InstructionParser.Parse(marvinCall, hash.Context).Operand!;
        Assert.AreEqual(marvin, parsed.Method!.DeclaringType);
    }
}
