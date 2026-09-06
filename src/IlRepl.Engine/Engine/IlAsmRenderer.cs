using System.Reflection;
using System.Text;

namespace IlRepl.Engine;

/// <summary>
/// Renders a session as ILAsm source: assembly references, a static class, one method per
/// <c>.method</c> definition, and a <c>Run</c> method with the cell's locals, arguments, blocks,
/// and instructions.
/// </summary>
public static class IlAsmRenderer
{
    /// <summary>
    /// Renders the session's current cell.
    /// </summary>
    /// <param name="session">The session.</param>
    /// <returns>ILAsm text.</returns>
    public static string Render(Session session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var cell = session.Cell;
        var sb = new StringBuilder();
        var assemblies = new SortedSet<string>(StringComparer.Ordinal) { "System.Runtime" };

        void Note(Type? type)
        {
            if (type is null || type.IsPrimitive || type == typeof(string) || type == typeof(object) || type.IsGenericParameter)
            {
                return;
            }

            if (type.IsByRef || type.IsPointer || type.IsArray)
            {
                Note(type.GetElementType());
                return;
            }

            assemblies.Add(TypeNameFormatter.AssemblyReferenceName(type));
            if (type.IsGenericType)
            {
                foreach (var a in type.GetGenericArguments())
                {
                    Note(a);
                }
            }
        }

        void NoteState(CellState state)
        {
            foreach (var l in state.Locals)
            {
                Note(l.Type);
            }

            foreach (var a in state.Arguments)
            {
                Note(a.Type);
            }

            foreach (var e in state.Entries)
            {
                Note(e.CatchType);
                switch (e.Instruction?.Operand)
                {
                    case Type t:
                        Note(t);
                        break;
                    case ResolvedMethod m:
                        Note(m.DeclaringType);
                        break;
                    case FieldInfo f:
                        Note(f.DeclaringType);
                        break;
                    default:
                        break;
                }
            }
        }

        foreach (var method in session.Methods)
        {
            Note(method.Signature.ReturnType);
            NoteState(method.State);
        }

        NoteState(cell);

        foreach (var a in assemblies)
        {
            sb.Append(".assembly extern ").Append(a).AppendLine(" {}");
        }

        sb.AppendLine(".assembly ilrepl_cell {}");
        sb.AppendLine(".module ilrepl_cell.dll");
        sb.AppendLine();
        sb.AppendLine(".class public abstract sealed auto ansi beforefieldinit IlRepl.Cell extends [System.Runtime]System.Object");
        sb.AppendLine("{");

        foreach (var method in session.Methods)
        {
            var signature = method.Signature;
            var methodParameters = string.Join(", ", signature.Parameters.Select((p, i) => TypeNameFormatter.IlAsm(p.Type) + " " + (p.Name ?? "arg" + i.ToString(System.Globalization.CultureInfo.InvariantCulture))));
            sb.Append("    .method public static ").Append(TypeNameFormatter.IlAsm(signature.ReturnType)).Append(' ').Append(signature.Name).Append('(').Append(methodParameters).AppendLine(") cil managed");
            sb.AppendLine("    {");
            RenderBody(sb, method.State);
            sb.AppendLine("    }");
            sb.AppendLine();
        }

        var generic = session.TypeParameterNames.Count > 0 ? "<" + string.Join(", ", session.TypeParameterNames) + ">" : "";
        var convention = cell.IsVarArg ? "vararg " : "";
        var parameters = string.Join(", ", cell.Arguments.Select((a, i) => TypeNameFormatter.IlAsm(a.Type) + " " + (a.Name ?? "arg" + i.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        sb.Append("    .method public static ").Append(convention).Append("object Run").Append(generic).Append('(').Append(parameters).AppendLine(") cil managed");
        sb.AppendLine("    {");
        RenderBody(sb, cell);
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static void RenderBody(StringBuilder sb, CellState state)
    {
        sb.AppendLine("        .maxstack 16");
        if (state.Locals.Count > 0)
        {
            var locals = state.Locals.Select((l, i) =>
                $"[{i}] {TypeNameFormatter.IlAsm(l.Type)}{(l.IsPinned ? " pinned" : "")} {l.Name ?? "V_" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            sb.Append("        .locals init (").Append(string.Join(", ", locals)).AppendLine(")");
        }

        var indent = 2;
        foreach (var e in state.Entries)
        {
            foreach (var l in e.Labels)
            {
                sb.Append(Pad(indent - 1)).Append(l).AppendLine(":");
            }

            switch (e.Kind)
            {
                case EntryKind.Instruction:
                    var instruction = e.Instruction!;
                    if (instruction.RetNull)
                    {
                        sb.Append(Pad(indent)).AppendLine("ldnull");
                    }
                    else if (instruction.RetBox is not null)
                    {
                        sb.Append(Pad(indent)).Append("box ").AppendLine(TypeNameFormatter.IlAsm(instruction.RetBox));
                    }

                    sb.Append(Pad(indent)).AppendLine(RenderInstruction(instruction));
                    break;
                case EntryKind.Block:
                    switch (e.Block)
                    {
                        case BlockKind.Try:
                            sb.Append(Pad(indent)).AppendLine(".try");
                            sb.Append(Pad(indent)).AppendLine("{");
                            indent++;
                            break;
                        case BlockKind.Catch:
                            indent--;
                            sb.Append(Pad(indent)).AppendLine("}");
                            sb.Append(Pad(indent)).Append("catch ").AppendLine(TypeNameFormatter.IlAsmDeclaring(e.CatchType ?? typeof(object)));
                            sb.Append(Pad(indent)).AppendLine("{");
                            indent++;
                            break;
                        case BlockKind.Filter:
                            indent--;
                            sb.Append(Pad(indent)).AppendLine("}");
                            sb.Append(Pad(indent)).AppendLine("filter");
                            sb.Append(Pad(indent)).AppendLine("{");
                            indent++;
                            break;
                        case BlockKind.FilterHandler:
                            indent--;
                            sb.Append(Pad(indent)).AppendLine("}");
                            sb.Append(Pad(indent)).AppendLine("{");
                            indent++;
                            break;
                        case BlockKind.Finally:
                            indent--;
                            sb.Append(Pad(indent)).AppendLine("}");
                            sb.Append(Pad(indent)).AppendLine("finally");
                            sb.Append(Pad(indent)).AppendLine("{");
                            indent++;
                            break;
                        case BlockKind.Fault:
                            indent--;
                            sb.Append(Pad(indent)).AppendLine("}");
                            sb.Append(Pad(indent)).AppendLine("fault");
                            sb.Append(Pad(indent)).AppendLine("{");
                            indent++;
                            break;
                        case BlockKind.End:
                            indent--;
                            sb.Append(Pad(indent)).AppendLine("}");
                            break;
                        default:
                            break;
                    }

                    break;
                default:
                    break;
            }
        }

        if (state.LastInstructionEndsFlow)
        {
            return;
        }

        if (!state.IsMethod)
        {
            if (state.Stack.Count == 0)
            {
                sb.Append(Pad(indent)).AppendLine("ldnull");
            }
            else if (state.Stack.Top is { IsValueType: true } valueType && valueType != typeof(NullReferenceMarker))
            {
                sb.Append(Pad(indent)).Append("box ").AppendLine(TypeNameFormatter.IlAsm(valueType));
            }
        }

        sb.Append(Pad(indent)).AppendLine("ret");
    }

    /// <summary>
    /// Renders one instruction with fully qualified operands.
    /// </summary>
    /// <param name="instruction">The instruction.</param>
    /// <returns>The ILAsm text for the instruction.</returns>
    public static string RenderInstruction(Instruction instruction)
    {
        ArgumentNullException.ThrowIfNull(instruction);
        var name = instruction.Op.Name;
        return instruction.Kind switch
        {
            OperandKind.String => name + " " + LiteralParser.Escape((string)instruction.Operand!),
            OperandKind.Type => name + " " + TypeNameFormatter.IlAsm((Type)instruction.Operand!),
            OperandKind.Method => name + " " + MethodIlAsm((ResolvedMethod)instruction.Operand!),
            OperandKind.Field => name + " " + FieldIlAsm((FieldInfo)instruction.Operand!),
            OperandKind.Token => name + " " + instruction.Operand switch
            {
                Type t => TypeNameFormatter.IlAsm(t),
                ResolvedMethod m => "method " + MethodIlAsm(m),
                FieldInfo f => "field " + FieldIlAsm(f),
                _ => "?",
            },
            OperandKind.Signature => name + " " + SignatureIlAsm((CalliSignature)instruction.Operand!),
            OperandKind.Labels => name + " (" + string.Join(", ", (string[])instruction.Operand!) + ")",
            _ => instruction.Text,
        };
    }

    private static string Pad(int level) => new(' ', level * 4);

    private static string MethodIlAsm(ResolvedMethod resolved)
    {
        if (resolved.Definition is { } definition)
        {
            return $"{TypeNameFormatter.IlAsm(definition.ReturnType)} IlRepl.Cell::{definition.Name}({string.Join(", ", definition.ParameterTypes.Select(TypeNameFormatter.IlAsm))})";
        }

        var method = resolved.Method!;
        var instance = method.IsStatic ? "" : "instance ";
        var vararg = method.CallingConvention.HasFlag(CallingConventions.VarArgs) ? "vararg " : "";
        var returnType = method is MethodInfo mi ? TypeNameFormatter.IlAsm(mi.ReturnType) : "void";
        var name = method is ConstructorInfo ? (method.IsStatic ? ".cctor" : ".ctor") : method.Name;
        if (method is MethodInfo g && g.IsGenericMethod)
        {
            name += "<" + string.Join(", ", g.GetGenericArguments().Select(TypeNameFormatter.IlAsm)) + ">";
        }

        var parameters = method.GetParameters().Select(p => TypeNameFormatter.IlAsm(p.ParameterType)).ToList();
        if (resolved.OptionalParameterTypes is not null)
        {
            parameters.Add("...");
            parameters.AddRange(resolved.OptionalParameterTypes.Select(TypeNameFormatter.IlAsm));
        }

        var declaring = method.DeclaringType is null ? "?" : TypeNameFormatter.IlAsmDeclaring(method.DeclaringType);
        return $"{instance}{vararg}{returnType} {declaring}::{name}({string.Join(", ", parameters)})";
    }

    private static string FieldIlAsm(FieldInfo field)
    {
        var declaring = field.DeclaringType is null ? "?" : TypeNameFormatter.IlAsmDeclaring(field.DeclaringType);
        return $"{TypeNameFormatter.IlAsm(field.FieldType)} {declaring}::{field.Name}";
    }

    private static string SignatureIlAsm(CalliSignature signature)
    {
        var sb = new StringBuilder();
        if (signature.IsUnmanaged)
        {
            sb.Append("unmanaged ").Append(signature.UnmanagedConvention.ToString().ToLowerInvariant()).Append(' ');
        }
        else
        {
            if ((signature.ManagedConvention & CallingConventions.HasThis) != 0)
            {
                sb.Append("instance ");
            }

            if ((signature.ManagedConvention & CallingConventions.VarArgs) != 0)
            {
                sb.Append("vararg ");
            }
        }

        sb.Append(TypeNameFormatter.IlAsm(signature.ReturnType)).Append('(');
        var parameters = signature.ParameterTypes.Select(TypeNameFormatter.IlAsm).ToList();
        if (signature.OptionalParameterTypes is not null)
        {
            parameters.Add("...");
            parameters.AddRange(signature.OptionalParameterTypes.Select(TypeNameFormatter.IlAsm));
        }

        sb.Append(string.Join(", ", parameters)).Append(')');
        return sb.ToString();
    }
}
