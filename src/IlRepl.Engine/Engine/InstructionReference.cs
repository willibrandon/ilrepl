using System.Reflection;
using System.Reflection.Emit;
using IlRepl.Engine.Binding;
using IlRepl.Protocol;

namespace IlRepl.Engine;

/// <summary>
/// Supplies offline instruction explanations shared by completion, analysis, and the opcode reference.
/// </summary>
public static class InstructionReference
{
    private const string ReferenceUrl = "https://ilrepl.dev/reference/opcodes/#";
    private static readonly Dictionary<string, InstructionHelp> Entries = OpcodeTable.Names
        .ToDictionary(name => name, Build, StringComparer.Ordinal);

    /// <summary>
    /// Retrieves the explanation of an accepted instruction.
    /// </summary>
    public static InstructionHelp For(string mnemonic) => Entries[mnemonic];

    /// <summary>
    /// Retrieves instruction help when the source names a complete opcode.
    /// </summary>
    public static InstructionHelp? Find(string mnemonic) => Entries.GetValueOrDefault(mnemonic);

    /// <summary>
    /// Presents resolved members and standalone signatures without replacing their original source.
    /// </summary>
    internal static string Syntax(BoundInstruction instruction, SnapshotBindingScope scope, string source)
    {
        var operand = instruction.Operand;
        var signature = operand.Method is { OptionalParameterTypes.Count: > 0 } ? null
            : operand.Method is { } method ? new MemberSpeller(scope).FullSignature(method.Method)
            : operand.Field is { } field ? new MemberSpeller(scope).FullSignature(field)
            : operand.Signature is { } calli ? SymbolRenderer.Signature(calli, scope.Pretty) : null;
        if (signature is null)
        {
            return source.Trim();
        }

        var mnemonic = instruction.Op.Name!;
        if (instruction.Op == OpCodes.Ldtoken)
        {
            mnemonic += operand.Method is not null ? " method" : operand.Field is not null ? " field" : "";
        }

        return Syntax(mnemonic, signature);
    }

    /// <summary>
    /// Formats a selected operand signature without declaration-only modifiers in the instruction syntax.
    /// </summary>
    internal static string Syntax(string mnemonic, string signature)
    {
        var operand = signature.StartsWith("static ", StringComparison.Ordinal) ? signature[7..] : signature;
        return mnemonic + " " + operand;
    }

    /// <summary>
    /// Describes a confirmed instruction using its own resolved operand and the shared stack transfer.
    /// </summary>
    public static InstructionHelp For(BoundInstruction instruction, EditingView context)
    {
        var help = For(instruction.Op.Name!);
        return help with
        {
            Syntax = Syntax(instruction, (SnapshotBindingScope)context.Scope, instruction.Text),
            StackEffect = StackTransitionText.Format(instruction, context),
            Notes = [.. help.Notes, .. ContextNotes(EditingStack.View(instruction, context.Scope), SymbolStackAlgebra.Instance)],
        };
    }

    /// <summary>
    /// Describes an analyzed source instruction using its established operand and stack facts.
    /// </summary>
    internal static InstructionHelp? For<T>(
        IReadOnlyList<FlowNode<T>> nodes,
        int position,
        FlowTypeRules<T> types,
        FlowState<T>? state,
        int? returnArity = null,
        FlowState<T>? outgoing = null) where T : class
    {
        var node = nodes[position];
        if (node.Instruction is not { } instruction)
        {
            return null;
        }

        var name = instruction.DecodedPrefixName ?? instruction.Op.Name!;
        var help = For(name);
        try
        {
            return Specialize(nodes, position, types, state, outgoing, help, instruction, returnArity);
        }
        catch (Exception exception) when (exception is TypeLoadException or ArgumentException or NotSupportedException)
        {
            // Invalid operands can have no runtime type, such as an array of managed pointers.
            return help with
            {
                Syntax = node.Synthetic ? instruction.Op.Name! : node.InstructionSyntax ?? node.Source.Trim(),
                Notes = [.. help.Notes, "This operand is invalid; a concrete stack effect is unavailable."],
            };
        }
    }

    private static InstructionHelp Specialize<T>(
        IReadOnlyList<FlowNode<T>> nodes,
        int position,
        FlowTypeRules<T> types,
        FlowState<T>? state,
        FlowState<T>? outgoing,
        InstructionHelp help,
        StackOperandView<T> instruction,
        int? returnArity)
        where T : class
    {
        var node = nodes[position];
        help = help with
        {
            Syntax = node.Synthetic ? instruction.Op.Name! : node.InstructionSyntax ?? node.Source.Trim(),
            Notes = [.. help.Notes, .. ContextNotes(instruction, types.Algebra)],
        };

        if (state?.Kind != AnalyzedStackKind.Known || outgoing?.Kind != AnalyzedStackKind.Known)
        {
            var note = state?.Invalid == true || outgoing?.Invalid == true
                ? "The current stack or operand is invalid; a concrete stack effect is unavailable."
                : "A concrete stack effect is unavailable until this instruction has a validated incoming stack.";
            return help with { Notes = [.. help.Notes, note] };
        }

        var pops = instruction.Op == OpCodes.Ret ? returnArity ?? Math.Min(1, state?.Values?.Length ?? 0)
            : StackTransfer<T>.PopCount(instruction);
        var incoming = state?.Kind == AnalyzedStackKind.Known && state.Values is { } values && values.Length >= pops
            ? values.TakeLast(pops).Select(value => value.Type).ToArray() : new T?[pops];
        if (instruction.Op.Name is "call" or "callvirt" or "calli" or "newobj")
        {
            var receiver = instruction.Op != OpCodes.Newobj && (instruction.IsInstance || instruction.HasImplicitThis) ? 1 : 0;
            if (receiver > 0)
            {
                var owner = instruction.DeclaringType;
                incoming[0] = owner is not null && types.Algebra.IsValueType(owner) ? types.Algebra.MakeByRef(owner) : owner;
                for (var prefix = position - 1; prefix >= 0; prefix--)
                {
                    if (nodes[prefix].Instruction is not { } preceding)
                    {
                        if (nodes[prefix].Block is not null)
                        {
                            break;
                        }

                        continue;
                    }

                    if (preceding.Op.OpCodeType != OpCodeType.Prefix && preceding.DecodedPrefixName is null)
                    {
                        break;
                    }

                    if (preceding.Op == OpCodes.Constrained && preceding.Type is { } constraint)
                    {
                        incoming[0] = types.Algebra.MakeByRef(constraint);
                    }
                }
            }

            for (var index = 0; index < instruction.ParameterTypes.Count; index++)
            {
                incoming[receiver + index] = instruction.ParameterTypes[index];
            }
        }

        var pushed = new StackTransfer<T>(types.Algebra).PushTypes(instruction, incoming);
        var effect = "[" + string.Join(", ", incoming.Select(types.Name)) + "] → ["
            + string.Join(", ", pushed.Select(types.Name)) + "]";
        return help with { StackEffect = effect };
    }

    /// <summary>
    /// Returns a stable per-opcode anchor for the generated reference.
    /// </summary>
    public static string Anchor(string mnemonic) => mnemonic.TrimEnd('.').Replace('.', '-');

    /// <summary>
    /// Links Reflection.Emit instructions to Microsoft documentation and metadata-only prefixes to the CLI specification.
    /// </summary>
    public static string ExternalUrl(string mnemonic)
    {
        if (mnemonic == "no.")
        {
            return "https://ecma-international.org/publications-and-standards/standards/ecma-335/";
        }

        var field = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .First(item => item.FieldType == typeof(OpCode) && ((OpCode)item.GetValue(null)!).Name == mnemonic);
        return "https://learn.microsoft.com/dotnet/api/system.reflection.emit.opcodes." + field.Name.ToLowerInvariant();
    }

    private static InstructionHelp Build(string name)
    {
        var opcode = OpcodeTable.BySourceName[name];
        var operand = opcode.IsSkipChecksPrefix ? "mask" : opcode.OperandType switch
        {
            OperandType.InlineNone => "",
            OperandType.InlineMethod => "method",
            OperandType.InlineField => "field",
            OperandType.InlineType => "type",
            OperandType.InlineTok => "type, method, or field",
            OperandType.InlineSig => "signature",
            OperandType.InlineString => "\"text\"",
            OperandType.InlineSwitch => "(label, ...)",
            OperandType.InlineBrTarget or OperandType.ShortInlineBrTarget => "label",
            OperandType.InlineVar or OperandType.ShortInlineVar => name.Contains("arg", StringComparison.Ordinal)
                ? "argument" : "local",
            _ => name == "unaligned." ? "1|2|4" : "value",
        };

        return new InstructionHelp(name, name + (operand.Length == 0 ? "" : " " + operand),
            OpcodeTable.StackTransition(opcode).Trim(), Explanation(name), Notes(name), ReferenceUrl + Anchor(name));
    }

    private static string Explanation(string name)
    {
        var family = name.EndsWith(".s", StringComparison.Ordinal) ? name[..^2] : name;
        if (family.StartsWith("ldarga", StringComparison.Ordinal))
        {
            return "Pushes a managed pointer to an argument, allowing access to the argument's storage.";
        }

        if (family.StartsWith("ldarg", StringComparison.Ordinal))
        {
            return "Loads an argument onto the evaluation stack. In instance methods, argument 0 is this.";
        }

        if (family.StartsWith("starg", StringComparison.Ordinal))
        {
            return "Pops a value into the specified argument's storage; its type must be assignable to that argument.";
        }

        if (family.StartsWith("ldloca", StringComparison.Ordinal))
        {
            return "Pushes a managed pointer to a local variable's storage.";
        }

        if (family.StartsWith("ldloc", StringComparison.Ordinal))
        {
            return "Loads the specified local variable onto the evaluation stack.";
        }

        if (family.StartsWith("stloc", StringComparison.Ordinal))
        {
            return "Pops a value into the specified local variable; its type must be assignable to that local.";
        }

        if (family.StartsWith("ldc.", StringComparison.Ordinal))
        {
            return "Pushes the encoded numeric constant onto the evaluation stack.";
        }

        if (family.StartsWith("conv.", StringComparison.Ordinal))
        {
            return Conversion(name);
        }

        if (family.StartsWith("ldind.", StringComparison.Ordinal))
        {
            return "Loads a value through a managed or unmanaged pointer using the instruction's storage type.";
        }

        if (family.StartsWith("stind.", StringComparison.Ordinal))
        {
            return "Stores the top value through the pointer below it using the instruction's storage type.";
        }

        if (family.StartsWith("ldelem", StringComparison.Ordinal) && family != "ldelema")
        {
            return "Pops an array and index, then loads that element using the specified element type.";
        }

        if (family.StartsWith("stelem", StringComparison.Ordinal))
        {
            return "Pops an array, index, and value, then stores the value into that array element.";
        }

        if (family.StartsWith("add", StringComparison.Ordinal) || family.StartsWith("sub", StringComparison.Ordinal)
            || family.StartsWith("mul", StringComparison.Ordinal))
        {
            var operation = family.StartsWith("add", StringComparison.Ordinal) ? "Adds the two operands"
                : family.StartsWith("sub", StringComparison.Ordinal) ? "Subtracts the top operand from the one below it"
                : "Multiplies the two operands";
            return operation + (family.Contains("ovf", StringComparison.Ordinal)
                ? ", throwing OverflowException if the integer result is out of range."
                : ". Integer overflow wraps; floating-point overflow produces infinity.");
        }

        if (FlowNumericRules.Comparison(family))
        {
            return Comparison(family);
        }

        return family switch
        {
            "nop" => "Does nothing and leaves the evaluation stack unchanged.",
            "break" => "Signals a breakpoint to the debugger; handling depends on the runtime and debugger.",
            "ldnull" => "Pushes a null object reference, which can be assigned to a reference type.",
            "ldstr" => "Pushes the string literal as an object reference.",
            "dup" => "Duplicates the top stack value. Both copies retain the same original producer.",
            "pop" => "Discards the top stack value.",
            "call" => "Invokes the method named by the operand directly, including an instance or virtual method's implementation.",
            "callvirt" => "Calls an instance method, using the receiver's runtime type for virtual or interface dispatch.",
            "calli" => "Calls the function pointer on top of the stack using the supplied calling convention and signature.",
            "newobj" => "Pops the constructor arguments and creates an initialized object or value; no receiver is supplied.",
            "ret" => "Returns from the current method. A non-void method requires exactly one assignable return value.",
            "jmp" => "Transfers directly to a method with a compatible signature, forwarding the current arguments.",
            "br" => "Transfers control to the target label without consuming stack values.",
            "brfalse" => "Pops a condition and branches when it is zero or a null reference or pointer.",
            "brtrue" => "Pops a condition and branches when it is nonzero or a non-null reference or pointer.",
            "switch" => "Pops an int32 index and branches to that zero-based entry; an out-of-range index falls through.",
            "div" => "Divides the operand below the top by the top operand; integer division truncates toward zero.",
            "div.un" => "Divides the operand below the top by the top operand, interpreting both integers as unsigned.",
            "rem" => "Computes the remainder of the operand below the top divided by the top operand.",
            "rem.un" => "Computes the remainder using unsigned integer operands.",
            "and" => "Computes the bitwise AND of two integer operands.",
            "or" => "Computes the bitwise OR of two integer operands.",
            "xor" => "Computes the bitwise exclusive OR of two integer operands.",
            "shl" => "Shifts the integer below the top to the left by the top value, filling low bits with zero.",
            "shr" => "Shifts a signed integer right, copying the sign bit into the vacated high bits.",
            "shr.un" => "Shifts an integer right logically, filling the vacated high bits with zero.",
            "neg" => "Negates a numeric value without checking integer overflow.",
            "not" => "Complements every bit of an integer value.",
            "box" => "Converts a value to its boxed representation; non-nullable value types are copied into an object.",
            "unbox" => "Returns a managed pointer to an unboxed value, allowing access to its storage rather than copying it.",
            "unbox.any" => "Extracts a boxed value; for a reference-type operand it performs the same check as castclass.",
            "castclass" => "Checks an object reference against a type, throwing InvalidCastException when incompatible; null stays null.",
            "isinst" => "Tests an object reference against a type, returning the reference on success or null on failure.",
            "newarr" => "Pops an integer length and creates a zero-based, one-dimensional array of the specified element type.",
            "ldlen" => "Pops an array reference and pushes its length as an unsigned native integer.",
            "ldelema" => "Pops an array and index and pushes a managed pointer to that element.",
            "ldfld" => "Loads the named instance field from the receiver on the stack.",
            "ldflda" => "Pushes a managed pointer to the named instance field's storage.",
            "stfld" => "Pops a receiver and value and stores the value into the named instance field.",
            "ldsfld" => "Loads the named static field; no instance receiver is needed.",
            "ldsflda" => "Pushes a managed pointer to the named static field's storage.",
            "stsfld" => "Pops a value and stores it into the named static field.",
            "ldobj" => "Loads a value of the specified type through a pointer.",
            "stobj" => "Copies the top value of the specified type into the storage addressed below it.",
            "cpobj" => "Copies the specified type's value from the source pointer to the destination pointer below it.",
            "initobj" => "Initializes the addressed storage to the type's default value without calling a constructor.",
            "ldftn" => "Pushes a native function pointer to the named method without invoking it.",
            "ldvirtftn" => "Pops a receiver and obtains the function pointer selected by virtual dispatch without invoking it.",
            "ldtoken" => "Pushes a runtime type, method, or field handle for the metadata operand.",
            "sizeof" => "Pushes the size in bytes of the specified type as int32; it does not consume an instance.",
            "throw" => "Pops and throws an exception object, transferring control to exception handling.",
            "rethrow" => "Rethrows the active exception from within its catch handler, preserving the original exception context.",
            "leave" => "Exits to the target label, empties the evaluation stack, and runs intervening finally handlers.",
            "endfinally" => "Ends a finally or fault handler and resumes the pending exception or leave operation.",
            "endfilter" => "Ends an exception filter; exactly one int32 result chooses rejection (0) or acceptance (1).",
            "localloc" => "Allocates local memory in the current stack frame and pushes a native pointer to it.",
            "cpblk" => "Copies a byte count from the source address to the destination address; overlapping ranges are unspecified.",
            "initblk" => "Fills a byte count at the destination address using the low byte of the supplied integer value.",
            "arglist" => "Pushes a handle to the current variable-argument list; the enclosing method must use vararg.",
            "mkrefany" => "Combines an address and a type token into a typed reference.",
            "refanyval" => "Extracts an address from a typed reference, checking that its type matches the operand.",
            "refanytype" => "Extracts the runtime type handle from a typed reference.",
            "ckfinite" => "Checks a floating-point value and throws ArithmeticException for NaN or infinity.",
            "constrained." => "Adapts the following callvirt to a type, or resolves a static virtual interface call or ldftn.",
            "tail." => "Marks the following call as a tail call, allowing transfer without retaining the current frame.",
            "readonly." => "Makes ldelema or an array Address call return a managed pointer with controlled mutability.",
            "volatile." => "Gives the following memory read acquire semantics or memory write release semantics.",
            "unaligned." => "Specifies that the following memory access can rely only on the supplied alignment (1, 2, or 4 bytes).",
            "no." => "Permits selected type, range, or null checks on the following instruction to be skipped; skipping is optional.",
            _ => throw new InvalidOperationException("Instruction explanation missing: " + name),
        };
    }

    private static string Conversion(string name)
    {
        var destination = name.Replace("conv.", "", StringComparison.Ordinal).Replace("ovf.", "", StringComparison.Ordinal);
        if (destination.EndsWith(".un", StringComparison.Ordinal))
        {
            destination = destination[..^3];
        }

        var type = destination switch
        {
            "i1" => "int8", "i2" => "int16", "i4" => "int32", "i8" => "int64", "i" => "native int",
            "u1" => "uint8", "u2" => "uint16", "u4" => "uint32", "u8" => "uint64", "u" => "native uint",
            "r4" => "float32", "r8" => "float64", _ => "floating point",
        };

        return "Converts the top value to " + type + (name.Contains("ovf", StringComparison.Ordinal)
            ? ", throwing OverflowException if it cannot be represented." : " without an overflow check.");
    }

    private static string Comparison(string name)
    {
        var relation = name.StartsWith("ceq", StringComparison.Ordinal) || name.StartsWith("beq", StringComparison.Ordinal) ? "equal to"
            : name.StartsWith("bne", StringComparison.Ordinal) ? "not equal to"
            : name.StartsWith("bge", StringComparison.Ordinal) ? "greater than or equal to"
            : name.StartsWith("ble", StringComparison.Ordinal) ? "less than or equal to"
            : name.StartsWith("cgt", StringComparison.Ordinal) || name.StartsWith("bgt", StringComparison.Ordinal) ? "greater than"
            : "less than";
        return (name.StartsWith('b') ? "Branches when" : "Pushes int32 1 when")
            + " the operand below the top is " + relation + " the top operand"
            + (name.StartsWith('b') ? "; otherwise falls through." : "; otherwise pushes int32 0.");
    }

    private static string[] Notes(string name)
    {
        var notes = new List<string>();
        if (name.EndsWith(".s", StringComparison.Ordinal))
        {
            notes.Add("The short form uses a smaller encoded operand; its operation is otherwise the same.");
        }

        if (name.Contains("ovf", StringComparison.Ordinal) && !name.StartsWith("conv", StringComparison.Ordinal))
        {
            notes.Add(name.EndsWith(".un", StringComparison.Ordinal)
                ? "Operands and the result range are unsigned integers." : "Operands and the result range are signed integers.");
        }

        if (name.StartsWith("conv", StringComparison.Ordinal))
        {
            notes.Add(name.EndsWith(".un", StringComparison.Ordinal)
                ? "The source integer is interpreted as unsigned; the destination type is specified separately."
                : "The destination type controls the result; integer sources are interpreted as signed for overflow checks.");
            if (name.Contains("ovf", StringComparison.Ordinal))
            {
                notes.Add("Floating-point sources truncate toward zero; .un does not change their interpretation.");
            }

            notes.Add("Small integers occupy int32 stack slots. Native integers have the current runtime's pointer width.");
        }

        if (FlowNumericRules.Comparison(name))
        {
            notes.Add(name.Contains(".un", StringComparison.Ordinal)
                ? "Integers are compared as unsigned. Floating-point unordered comparisons (NaN) make the condition true."
                : "Integer ordering is signed. Floating-point comparisons involving NaN make the condition false.");
            if (name == "cgt.un")
            {
                notes.Add("Object references also support the non-null test against null.");
            }
        }

        if (name is "call" or "callvirt" or "calli")
        {
            notes.Add("Push the receiver first when required, then arguments in signature order; a void return pushes nothing.");
        }

        if (name == "newobj")
        {
            notes.Add("Push constructor arguments in signature order. A constructor has a void signature, but newobj pushes the instance.");
        }

        if (name == "callvirt")
        {
            notes.Add("Nonvirtual instance methods are also allowed. An ordinary null object receiver throws NullReferenceException.");
        }

        if (name == "call")
        {
            notes.Add("An instance call still needs a receiver; call does not itself provide callvirt's null check.");
        }

        if (name == "calli")
        {
            notes.Add("Push the function pointer after the receiver and arguments. This operation is unverifiable.");
        }

        if (name is "box" or "unbox.any")
        {
            notes.Add("Nullable boxing produces null for no value, otherwise a boxed underlying value;"
                + " generic behavior depends on the type.");
        }

        if (name == "box")
        {
            notes.Add("A reference-type operand leaves the reference unchanged.");
        }

        if (name is "unbox" or "unbox.any")
        {
            notes.Add("Unboxing checks the boxed type; it does not perform a numeric conversion. A mismatched"
                + " type throws InvalidCastException.");
        }

        if (name == "unbox")
        {
            notes.Add("The result has controlled mutability. Unboxing Nullable<T> can require newly manufactured nullable storage.");
        }

        if (name is "unbox" or "unbox.any")
        {
            notes.Add("Null becomes an empty nullable value for Nullable<T>; a non-nullable value-type"
                + " operand throws NullReferenceException.");
        }

        if (name == "unbox.any")
        {
            notes.Add("A reference-type operand preserves null. With a generic operand, the actual type determines the operation.");
        }

        if (name == "constrained.")
        {
            notes.Add("For callvirt, the receiver is a managed pointer to the constrained type; boxing occurs only when required.");
            notes.Add("For call or ldftn, the target must be a static virtual interface method implemented by the constrained type.");
        }

        if (name == "tail.")
        {
            notes.Add("Prefix call, callvirt, or calli; only its arguments may remain. Follow the call with"
                + " ret outside protected regions.");
        }

        if (name == "readonly.")
        {
            notes.Add("Suppresses the exact array-element type check, permitting covariant array reads.");
            notes.Add("Indirect writes through the result are unverifiable; field stores and mutating receiver calls are permitted.");
        }

        if (name is "unaligned." or "volatile.")
        {
            notes.Add("Applies to indirect loads/stores, instance-field access, ldobj/stobj, cpblk, or initblk.");
        }

        if (name == "volatile.")
        {
            notes.Add("Also applies to ldsfld/stsfld. It does not make an otherwise non-atomic access atomic or replace a lock.");
        }

        if (name == "no.")
        {
            notes.Add("Mask bits 1, 2, and 4 select type, range, and null checks. Valid targets depend on the"
                + " selected checks; unverifiable.");
        }

        if (name is "tail." or "constrained." or "readonly." or "volatile." or "unaligned." or "no.")
        {
            notes.Add("Prefixes attach to the following instruction. Branches must target the first prefix,"
                + " not the middle of the sequence.");
        }

        if (name is "div" or "div.un" or "rem" or "rem.un")
        {
            notes.Add("Integer division by zero throws DivideByZeroException.");
        }

        if (name == "div")
        {
            notes.Add("Floating-point division follows IEEE floating-point rules.");
        }

        if (name == "rem")
        {
            notes.Add("Uses a quotient truncated toward zero; floating-point rem differs from Math.IEEERemainder.");
        }

        if (name == "no.")
        {
            notes.Add("Type checks (1): castclass, unbox, ldelema, stelem, stelem.ref.");
            notes.Add("Range checks (2): ldelem.*, stelem.*, ldelema.");
            notes.Add("Null checks (4): those array operations, ldfld, stfld, callvirt, ldvirtftn.");
            notes.Add("Every selected check must be valid for the target instruction.");
        }

        if (name == "ret")
        {
            notes.Add("A top-level ilrepl cell may return zero or one value; managed pointers cannot escape the cell.");
        }

        if (name.StartsWith("ldelem", StringComparison.Ordinal) || name.StartsWith("stelem", StringComparison.Ordinal))
        {
            notes.Add("The index is int32 or native int; null and bounds checks apply. Small integer loads widen to int32.");
        }

        return notes.ToArray();
    }

    private static IEnumerable<string> ContextNotes<T>(StackOperandView<T> instruction, IStackTypeAlgebra<T> algebra) where T : class
    {
        if (instruction.Op == OpCodes.Callvirt && instruction.MethodIsVirtual == false)
        {
            yield return "This resolved instance method is nonvirtual, so this call does not select an override.";
        }

        if (instruction.Op == OpCodes.Box && instruction.Type is { } type)
        {
            yield return algebra.IsGenericParameter(type) ? "The actual generic type determines whether boxing is needed."
                : algebra.IsValueType(type) ? "This operand is a value type."
                : "This operand is a reference type; its reference is preserved.";
        }
    }
}
