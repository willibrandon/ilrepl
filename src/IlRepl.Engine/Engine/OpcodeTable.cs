using System.Reflection;
using System.Reflection.Emit;

namespace IlRepl.Engine;

/// <summary>
/// Every CIL opcode the runtime knows about, keyed by its ILAsm name, with a one-line description and its stack transition.
/// </summary>
public static class OpcodeTable
{
    /// <summary>
    /// All opcodes by ILAsm name.
    /// </summary>
    public static IReadOnlyDictionary<string, OpCode> ByName { get; } = Build();

    /// <summary>
    /// All opcode names in ordinal order.
    /// </summary>
    public static IReadOnlyList<string> Names { get; } = ByName.Keys.OrderBy(n => n, StringComparer.Ordinal).ToArray();

    /// <summary>
    /// The encoded value of the <c>no.</c> prefix, which Reflection.Emit does not describe.
    /// </summary>
    public const ushort NoPrefixValue = 0xFE19;

    /// <summary>
    /// Every opcode that can appear in a method body, keyed by its encoded value. The reserved
    /// <c>prefixN</c> values are left out on purpose: a reader that meets one has found a fault.
    /// </summary>
    public static IReadOnlyDictionary<ushort, IlOpcode> ByValue { get; } = BuildByValue();

    /// <summary>
    /// Looks up an opcode by its encoded value.
    /// </summary>
    /// <param name="value">The byte for a one-byte opcode, <c>0xFExx</c> for a two-byte one.</param>
    /// <param name="opcode">The opcode when found.</param>
    /// <returns>True when the value encodes an instruction or prefix.</returns>
    public static bool TryGetByValue(ushort value, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IlOpcode? opcode) => ByValue.TryGetValue(value, out opcode);

    /// <summary>
    /// The number of operand bytes an operand layout takes, or -1 for <see cref="OperandType.InlineSwitch"/>.
    /// </summary>
    /// <param name="type">The operand layout.</param>
    /// <returns>The byte count.</returns>
    public static int OperandSize(OperandType type) => type switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineI or OperandType.InlineMethod
            or OperandType.InlineSig or OperandType.InlineString or OperandType.InlineTok or OperandType.InlineType
            or OperandType.ShortInlineR => 4,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        OperandType.InlineSwitch => -1,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "unknown operand type"),
    };

    /// <summary>
    /// Looks up an opcode by name.
    /// </summary>
    /// <param name="name">The ILAsm name, for example <c>ldc.i4.s</c>.</param>
    /// <param name="opcode">The opcode when found.</param>
    /// <returns>True when the name is a known opcode.</returns>
    public static bool TryGet(string name, out OpCode opcode)
    {
        ArgumentNullException.ThrowIfNull(name);
        return ByName.TryGetValue(name, out opcode);
    }

    /// <summary>
    /// A short description of what the opcode does.
    /// </summary>
    /// <param name="opcode">The opcode.</param>
    /// <returns>The description, or an empty string for reserved prefixes.</returns>
    public static string Describe(OpCode opcode) => Descriptions.TryGetValue(opcode.Name ?? "", out var s) ? s : "";

    /// <summary>
    /// The stack transition as ILAsm writes it: what is popped, an arrow, what is pushed.
    /// </summary>
    /// <param name="opcode">The opcode.</param>
    /// <returns>For example <c>i i → i</c> for <c>add</c> on two integers.</returns>
    public static string StackTransition(OpCode opcode) => $"{Pop(opcode.StackBehaviourPop),-9} → {Push(opcode.StackBehaviourPush)}";

    /// <summary>
    /// True for the reserved <c>prefixN</c> pseudo-opcodes that cannot be written in IL.
    /// </summary>
    /// <param name="name">The opcode name.</param>
    /// <returns>True when the name is reserved.</returns>
    public static bool IsReserved(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return name.StartsWith("prefix", StringComparison.Ordinal);
    }

    private static Dictionary<string, OpCode> Build()
    {
        var d = new Dictionary<string, OpCode>(StringComparer.Ordinal);
        foreach (var f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (f.FieldType != typeof(OpCode))
            {
                continue;
            }

            var op = (OpCode)f.GetValue(null)!;
            d[op.Name!] = op;
        }

        return d;
    }

    private static Dictionary<ushort, IlOpcode> BuildByValue()
    {
        var d = new Dictionary<ushort, IlOpcode>();
        foreach (var op in ByName.Values)
        {
            if (op.Name is null || IsReserved(op.Name))
            {
                continue;
            }

            d[unchecked((ushort)op.Value)] = new IlOpcode(unchecked((ushort)op.Value), op.Name, op.OperandType, op);
        }

        d[NoPrefixValue] = new IlOpcode(NoPrefixValue, "no.", OperandType.ShortInlineI, null);
        return d;
    }

    private static string Pop(StackBehaviour b) => b switch
    {
        StackBehaviour.Pop0 => "",
        StackBehaviour.Pop1 => "1",
        StackBehaviour.Popi => "i",
        StackBehaviour.Popref => "ref",
        StackBehaviour.Pop1_pop1 => "1 1",
        StackBehaviour.Popi_pop1 => "i 1",
        StackBehaviour.Popi_popi => "i i",
        StackBehaviour.Popi_popi8 => "i i8",
        StackBehaviour.Popi_popr4 => "i r4",
        StackBehaviour.Popi_popr8 => "i r8",
        StackBehaviour.Popref_pop1 => "ref 1",
        StackBehaviour.Popref_popi => "ref i",
        StackBehaviour.Popi_popi_popi => "i i i",
        StackBehaviour.Popref_popi_popi => "ref i i",
        StackBehaviour.Popref_popi_popi8 => "ref i i8",
        StackBehaviour.Popref_popi_popr4 => "ref i r4",
        StackBehaviour.Popref_popi_popr8 => "ref i r8",
        StackBehaviour.Popref_popi_popref => "ref i ref",
        StackBehaviour.Popref_popi_pop1 => "ref i 1",
        StackBehaviour.Varpop => "…",
        _ => "?",
    };

    private static string Push(StackBehaviour b) => b switch
    {
        StackBehaviour.Push0 => "",
        StackBehaviour.Push1 => "1",
        StackBehaviour.Push1_push1 => "1 1",
        StackBehaviour.Pushi => "i",
        StackBehaviour.Pushi8 => "i8",
        StackBehaviour.Pushr4 => "r4",
        StackBehaviour.Pushr8 => "r8",
        StackBehaviour.Pushref => "ref",
        StackBehaviour.Varpush => "…",
        _ => "?",
    };

    private static readonly Dictionary<string, string> Descriptions = new(StringComparer.Ordinal)
    {
        ["nop"] = "do nothing",
        ["break"] = "breakpoint trap",
        ["ldarg.0"] = "push argument 0", ["ldarg.1"] = "push argument 1", ["ldarg.2"] = "push argument 2", ["ldarg.3"] = "push argument 3",
        ["ldloc.0"] = "push local 0", ["ldloc.1"] = "push local 1", ["ldloc.2"] = "push local 2", ["ldloc.3"] = "push local 3",
        ["stloc.0"] = "pop into local 0", ["stloc.1"] = "pop into local 1", ["stloc.2"] = "pop into local 2", ["stloc.3"] = "pop into local 3",
        ["ldarg.s"] = "push argument (byte index)", ["ldarga.s"] = "push address of argument", ["starg.s"] = "pop into argument",
        ["ldloc.s"] = "push local (byte index)", ["ldloca.s"] = "push address of local", ["stloc.s"] = "pop into local",
        ["ldnull"] = "push null reference",
        ["ldc.i4.m1"] = "push int32 -1", ["ldc.i4.0"] = "push int32 0", ["ldc.i4.1"] = "push int32 1", ["ldc.i4.2"] = "push int32 2",
        ["ldc.i4.3"] = "push int32 3", ["ldc.i4.4"] = "push int32 4", ["ldc.i4.5"] = "push int32 5", ["ldc.i4.6"] = "push int32 6",
        ["ldc.i4.7"] = "push int32 7", ["ldc.i4.8"] = "push int32 8",
        ["ldc.i4.s"] = "push int32 (int8 immediate)", ["ldc.i4"] = "push int32 immediate", ["ldc.i8"] = "push int64 immediate",
        ["ldc.r4"] = "push float32 immediate", ["ldc.r8"] = "push float64 immediate",
        ["dup"] = "duplicate top of stack", ["pop"] = "discard top of stack",
        ["jmp"] = "jump to method (tail transfer)", ["call"] = "call method", ["calli"] = "call through function pointer", ["ret"] = "return",
        ["br.s"] = "branch (short)", ["brfalse.s"] = "branch if false, null, or zero", ["brtrue.s"] = "branch if true or non-null",
        ["beq.s"] = "branch if equal", ["bge.s"] = "branch if >=", ["bgt.s"] = "branch if >", ["ble.s"] = "branch if <=", ["blt.s"] = "branch if <",
        ["bne.un.s"] = "branch if != (unordered)", ["bge.un.s"] = "branch if >= (unsigned or unordered)", ["bgt.un.s"] = "branch if > (unsigned or unordered)",
        ["ble.un.s"] = "branch if <= (unsigned or unordered)", ["blt.un.s"] = "branch if < (unsigned or unordered)",
        ["br"] = "branch", ["brfalse"] = "branch if false, null, or zero", ["brtrue"] = "branch if true or non-null",
        ["beq"] = "branch if equal", ["bge"] = "branch if >=", ["bgt"] = "branch if >", ["ble"] = "branch if <=", ["blt"] = "branch if <",
        ["bne.un"] = "branch if != (unordered)", ["bge.un"] = "branch if >= (unsigned or unordered)", ["bgt.un"] = "branch if > (unsigned or unordered)",
        ["ble.un"] = "branch if <= (unsigned or unordered)", ["blt.un"] = "branch if < (unsigned or unordered)",
        ["switch"] = "jump table on int32",
        ["ldind.i1"] = "load int8 through pointer", ["ldind.u1"] = "load uint8 through pointer", ["ldind.i2"] = "load int16 through pointer",
        ["ldind.u2"] = "load uint16 through pointer", ["ldind.i4"] = "load int32 through pointer", ["ldind.u4"] = "load uint32 through pointer",
        ["ldind.i8"] = "load int64 through pointer", ["ldind.i"] = "load native int through pointer", ["ldind.r4"] = "load float32 through pointer",
        ["ldind.r8"] = "load float64 through pointer", ["ldind.ref"] = "load object reference through pointer",
        ["stind.ref"] = "store object reference through pointer", ["stind.i1"] = "store int8 through pointer", ["stind.i2"] = "store int16 through pointer",
        ["stind.i4"] = "store int32 through pointer", ["stind.i8"] = "store int64 through pointer", ["stind.r4"] = "store float32 through pointer",
        ["stind.r8"] = "store float64 through pointer", ["stind.i"] = "store native int through pointer",
        ["add"] = "add", ["sub"] = "subtract", ["mul"] = "multiply", ["div"] = "divide", ["div.un"] = "divide unsigned", ["rem"] = "remainder",
        ["rem.un"] = "remainder unsigned", ["and"] = "bitwise and", ["or"] = "bitwise or", ["xor"] = "bitwise xor", ["shl"] = "shift left",
        ["shr"] = "shift right (arithmetic)", ["shr.un"] = "shift right (logical)", ["neg"] = "negate", ["not"] = "bitwise complement",
        ["conv.i1"] = "convert to int8", ["conv.i2"] = "convert to int16", ["conv.i4"] = "convert to int32", ["conv.i8"] = "convert to int64",
        ["conv.r4"] = "convert to float32", ["conv.r8"] = "convert to float64", ["conv.u4"] = "convert to uint32", ["conv.u8"] = "convert to uint64",
        ["callvirt"] = "call virtual method on object", ["cpobj"] = "copy value type", ["ldobj"] = "load value type through pointer",
        ["ldstr"] = "push string literal", ["newobj"] = "allocate object and call constructor", ["castclass"] = "cast (throws on failure)", ["isinst"] = "type test (null when it fails)",
        ["conv.r.un"] = "convert unsigned integer to float", ["unbox"] = "unbox to value type address", ["throw"] = "throw exception",
        ["ldfld"] = "load instance field", ["ldflda"] = "load instance field address", ["stfld"] = "store instance field",
        ["ldsfld"] = "load static field", ["ldsflda"] = "load static field address", ["stsfld"] = "store static field",
        ["stobj"] = "store value type through pointer",
        ["conv.ovf.i1.un"] = "convert to int8 (unsigned source, overflow check)", ["conv.ovf.i2.un"] = "convert to int16 (unsigned source, overflow check)",
        ["conv.ovf.i4.un"] = "convert to int32 (unsigned source, overflow check)", ["conv.ovf.i8.un"] = "convert to int64 (unsigned source, overflow check)",
        ["conv.ovf.u1.un"] = "convert to uint8 (unsigned source, overflow check)", ["conv.ovf.u2.un"] = "convert to uint16 (unsigned source, overflow check)",
        ["conv.ovf.u4.un"] = "convert to uint32 (unsigned source, overflow check)", ["conv.ovf.u8.un"] = "convert to uint64 (unsigned source, overflow check)",
        ["conv.ovf.i.un"] = "convert to native int (unsigned source, overflow check)", ["conv.ovf.u.un"] = "convert to native uint (unsigned source, overflow check)",
        ["box"] = "box value type", ["newarr"] = "allocate array", ["ldlen"] = "push array length",
        ["ldelema"] = "push element address", ["ldelem.i1"] = "load int8 element", ["ldelem.u1"] = "load uint8 element", ["ldelem.i2"] = "load int16 element",
        ["ldelem.u2"] = "load uint16 element", ["ldelem.i4"] = "load int32 element", ["ldelem.u4"] = "load uint32 element", ["ldelem.i8"] = "load int64 element",
        ["ldelem.i"] = "load native int element", ["ldelem.r4"] = "load float32 element", ["ldelem.r8"] = "load float64 element", ["ldelem.ref"] = "load object element",
        ["stelem.i"] = "store native int element", ["stelem.i1"] = "store int8 element", ["stelem.i2"] = "store int16 element", ["stelem.i4"] = "store int32 element",
        ["stelem.i8"] = "store int64 element", ["stelem.r4"] = "store float32 element", ["stelem.r8"] = "store float64 element", ["stelem.ref"] = "store object element",
        ["ldelem"] = "load element of type", ["stelem"] = "store element of type", ["unbox.any"] = "unbox, or castclass for reference types",
        ["conv.ovf.i1"] = "convert to int8 (overflow check)", ["conv.ovf.u1"] = "convert to uint8 (overflow check)", ["conv.ovf.i2"] = "convert to int16 (overflow check)",
        ["conv.ovf.u2"] = "convert to uint16 (overflow check)", ["conv.ovf.i4"] = "convert to int32 (overflow check)", ["conv.ovf.u4"] = "convert to uint32 (overflow check)",
        ["conv.ovf.i8"] = "convert to int64 (overflow check)", ["conv.ovf.u8"] = "convert to uint64 (overflow check)",
        ["refanyval"] = "typed reference to address", ["ckfinite"] = "throw if NaN or infinity", ["mkrefany"] = "make typed reference",
        ["ldtoken"] = "push runtime handle", ["conv.u2"] = "convert to uint16", ["conv.u1"] = "convert to uint8", ["conv.i"] = "convert to native int",
        ["conv.ovf.i"] = "convert to native int (overflow check)", ["conv.ovf.u"] = "convert to native uint (overflow check)",
        ["add.ovf"] = "add (overflow check)", ["add.ovf.un"] = "add unsigned (overflow check)", ["mul.ovf"] = "multiply (overflow check)",
        ["mul.ovf.un"] = "multiply unsigned (overflow check)", ["sub.ovf"] = "subtract (overflow check)", ["sub.ovf.un"] = "subtract unsigned (overflow check)",
        ["endfinally"] = "end finally or fault handler", ["leave"] = "exit protected region", ["leave.s"] = "exit protected region (short)",
        ["stind.i"] = "store native int through pointer", ["conv.u"] = "convert to native uint",
        ["prefix7"] = "reserved", ["prefix6"] = "reserved", ["prefix5"] = "reserved", ["prefix4"] = "reserved", ["prefix3"] = "reserved",
        ["prefix2"] = "reserved", ["prefix1"] = "reserved", ["prefixref"] = "reserved",
        ["arglist"] = "push argument list handle (vararg cells)", ["ceq"] = "compare equal, push int32", ["cgt"] = "compare greater, push int32",
        ["cgt.un"] = "compare greater (unsigned or unordered), push int32", ["clt"] = "compare less, push int32", ["clt.un"] = "compare less (unsigned or unordered), push int32",
        ["ldftn"] = "push method pointer", ["ldvirtftn"] = "push virtual method pointer",
        ["ldarg"] = "push argument", ["ldarga"] = "push argument address", ["starg"] = "pop into argument",
        ["ldloc"] = "push local", ["ldloca"] = "push local address", ["stloc"] = "pop into local",
        ["localloc"] = "allocate stack memory", ["endfilter"] = "end exception filter",
        ["unaligned."] = "prefix: unaligned access", ["volatile."] = "prefix: volatile access", ["tail."] = "prefix: tail call",
        ["initobj"] = "zero-initialize value type at address", ["constrained."] = "prefix: constrained callvirt", ["cpblk"] = "copy memory block",
        ["initblk"] = "fill memory block", ["rethrow"] = "rethrow current exception", ["sizeof"] = "push size of type",
        ["refanytype"] = "typed reference to type handle", ["readonly."] = "prefix: readonly ldelema",
    };
}
