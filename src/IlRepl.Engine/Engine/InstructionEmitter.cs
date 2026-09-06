using System.Reflection;
using System.Reflection.Emit;

namespace IlRepl.Engine;

/// <summary>
/// Emits a parsed <see cref="Instruction"/> through an <see cref="ILGenerator"/>, picking the
/// overload that matches the operand kind.
/// </summary>
public static class InstructionEmitter
{
    /// <summary>
    /// Emits one instruction.
    /// </summary>
    /// <param name="il">The generator for the cell method.</param>
    /// <param name="instruction">The instruction.</param>
    /// <param name="locals">The declared locals, by index.</param>
    /// <param name="labels">The defined labels, by name.</param>
    public static void Emit(ILGenerator il, Instruction instruction, IReadOnlyList<LocalBuilder> locals, IReadOnlyDictionary<string, Label> labels)
    {
        ArgumentNullException.ThrowIfNull(il);
        ArgumentNullException.ThrowIfNull(instruction);
        ArgumentNullException.ThrowIfNull(locals);
        ArgumentNullException.ThrowIfNull(labels);
        var op = instruction.Op;

        if (op == OpCodes.Endfilter)
        {
            // ILGenerator emits endfilter itself when the handler block begins.
            return;
        }

        if (op == OpCodes.Ret)
        {
            if (instruction.RetPops == 0)
            {
                il.Emit(OpCodes.Ldnull);
            }
            else if (instruction.RetBox is not null)
            {
                il.Emit(OpCodes.Box, instruction.RetBox);
            }

            il.Emit(OpCodes.Ret);
            return;
        }

        switch (instruction.Kind)
        {
            case OperandKind.None:
                il.Emit(op);
                break;
            case OperandKind.SByte:
                il.Emit(op, (sbyte)instruction.Operand!);
                break;
            case OperandKind.Byte:
                il.Emit(op, (byte)instruction.Operand!);
                break;
            case OperandKind.Int32:
                il.Emit(op, (int)instruction.Operand!);
                break;
            case OperandKind.Int64:
                il.Emit(op, (long)instruction.Operand!);
                break;
            case OperandKind.Single:
                il.Emit(op, (float)instruction.Operand!);
                break;
            case OperandKind.Double:
                il.Emit(op, (double)instruction.Operand!);
                break;
            case OperandKind.String:
                il.Emit(op, (string)instruction.Operand!);
                break;
            case OperandKind.Label:
                il.Emit(op, labels[(string)instruction.Operand!]);
                break;
            case OperandKind.Labels:
                il.Emit(op, ((string[])instruction.Operand!).Select(l => labels[l]).ToArray());
                break;
            case OperandKind.Local:
                il.Emit(op, locals[(int)instruction.Operand!]);
                break;
            case OperandKind.Argument:
                if (op.OperandType == OperandType.ShortInlineVar)
                {
                    il.Emit(op, (byte)(int)instruction.Operand!);
                }
                else
                {
                    il.Emit(op, (short)(int)instruction.Operand!);
                }

                break;
            case OperandKind.Type:
                il.Emit(op, (Type)instruction.Operand!);
                break;
            case OperandKind.Field:
                il.Emit(op, (FieldInfo)instruction.Operand!);
                break;
            case OperandKind.Method:
                EmitMethod(il, op, (ResolvedMethod)instruction.Operand!);
                break;
            case OperandKind.Token:
                switch (instruction.Operand)
                {
                    case Type t:
                        il.Emit(op, t);
                        break;
                    case FieldInfo f:
                        il.Emit(op, f);
                        break;
                    case ConstructorInfo c:
                        il.Emit(op, c);
                        break;
                    case MethodInfo m:
                        il.Emit(op, m);
                        break;
                    default:
                        throw new ReplException("unsupported token operand");
                }

                break;
            case OperandKind.Signature:
                EmitCalli(il, (CalliSignature)instruction.Operand!);
                break;
            default:
                throw new ReplException($"unsupported operand kind {instruction.Kind}");
        }
    }

    private static void EmitMethod(ILGenerator il, OpCode op, ResolvedMethod resolved)
    {
        switch (resolved.Method)
        {
            case ConstructorInfo constructor:
                il.Emit(op, constructor);
                break;
            case MethodInfo method when resolved.OptionalParameterTypes is not null:
                il.EmitCall(op, method, resolved.OptionalParameterTypes);
                break;
            case MethodInfo method:
                il.Emit(op, method);
                break;
            default:
                throw new ReplException("unsupported method operand");
        }
    }

    private static void EmitCalli(ILGenerator il, CalliSignature signature)
    {
        if (signature.IsUnmanaged)
        {
            il.EmitCalli(OpCodes.Calli, signature.UnmanagedConvention, signature.ReturnType, signature.ParameterTypes);
        }
        else
        {
            il.EmitCalli(OpCodes.Calli, signature.ManagedConvention, signature.ReturnType, signature.ParameterTypes, signature.OptionalParameterTypes);
        }
    }
}
