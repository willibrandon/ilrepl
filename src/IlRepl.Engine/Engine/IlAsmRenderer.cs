using System.Globalization;
using System.Reflection;
using System.Text;
using IlRepl.Engine.Binding;

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

            var definition = type.IsGenericType ? type.GetGenericTypeDefinition() : type;
            if (!TypeRelations.IsSessionType(definition))
            {
                assemblies.Add(TypeNameFormatter.AssemblyReferenceName(type));
            }

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
                NoteExact(l.ExactType);
            }

            foreach (var a in state.Arguments)
            {
                Note(a.Type);
                NoteExact(a.ExactType);
            }

            foreach (var e in state.Entries)
            {
                Note(e.CatchType);
                NoteExact(e.Instruction?.ExactTypeOperand);
                if (e.Instruction?.Operand is CalliSignature { ExactSymbol: { } exactSignature })
                {
                    NoteExact(exactSignature.ReturnType);
                    foreach (var parameter in exactSignature.Parameters)
                    {
                        NoteExact(parameter);
                    }
                }

                switch (e.Instruction?.Operand)
                {
                    case Type t:
                        Note(t);
                        break;
                    case ResolvedMethod m:
                        Note(m.DeclaringType);
                        NoteExact(m.ExactDeclaringType);
                        if ((m.Definition ?? m.Declared) is { } signature)
                        {
                            NoteSignature(signature);
                        }
                        else if (m.Method is { } runtime)
                        {
                            foreach (var type in SymbolReferences.Method(RuntimeSymbolImporter.Import(DefinitionOf(runtime))))
                            {
                                NoteExact(type);
                            }
                        }

                        foreach (var argument in m.InstantiationArguments)
                        {
                            Note(argument);
                        }

                        foreach (var argument in m.ExactGenericArguments ?? [])
                        {
                            NoteExact(argument);
                        }

                        break;
                    case FieldInfo f:
                        Note(f.DeclaringType);
                        NoteExact(e.Instruction?.ExactFieldDeclaringType);
                        foreach (var type in SymbolReferences.Field(RuntimeSymbolImporter.Import(DefinitionOf(f))))
                        {
                            NoteExact(type);
                        }

                        break;
                    default:
                        break;
                }
            }
        }

        void NoteSignature(MethodSignature signature)
        {
            Note(signature.ReturnType);
            NoteExact(signature.ExactReturnType);
            foreach (var modifier in signature.ReturnRequiredModifiers.Concat(signature.ReturnOptionalModifiers))
            {
                Note(modifier);
            }

            foreach (var parameter in signature.Parameters)
            {
                Note(parameter.Type);
                NoteExact(parameter.ExactType);
                foreach (var modifier in parameter.RequiredModifiers.Concat(parameter.OptionalModifiers))
                {
                    Note(modifier);
                }
            }
        }

        void NoteExact(TypeSymbol? type)
        {
            if (type is null)
            {
                return;
            }

            foreach (var runtime in RuntimeSymbolTypes.Materialized(type))
            {
                Note(runtime);
            }
        }

        foreach (var method in session.Methods)
        {
            NoteSignature(method.Signature);
            NoteState(method.State);
        }

        foreach (var family in session.Types)
        {
            foreach (var declaration in family.Declaration.Family)
            {
                NoteDeclaration(declaration, Note, NoteExact, NoteState);
            }
        }

        NoteState(cell);

        foreach (var a in assemblies)
        {
            sb.Append(".assembly extern ").Append(a).AppendLine(" {}");
        }

        sb.AppendLine(".assembly ilrepl_cell {}");
        sb.AppendLine(".module ilrepl_cell.dll");
        sb.AppendLine();
        foreach (var family in session.Types)
        {
            RenderType(sb, family.Declaration, 0);
            sb.AppendLine();
        }

        sb.AppendLine(".class public abstract sealed auto ansi beforefieldinit IlRepl.Cell extends [System.Runtime]System.Object");
        sb.AppendLine("{");

        foreach (var method in session.Methods)
        {
            var signature = method.Signature;
            var methodParameters = string.Join(", ", signature.Parameters.Select((parameter, index) =>
                DeclarationType(parameter) + " " + TypeNameFormatter.IlAsmIdentifier(
                    parameter.Name ?? "arg" + index.ToString(CultureInfo.InvariantCulture))));
            sb.Append("    .method public static ")
                .Append(DeclarationReturnType(signature)).Append(' ')
                .Append(TypeNameFormatter.IlAsmIdentifier(signature.Name)).Append('(').Append(methodParameters)
                .AppendLine(") cil managed");
            sb.AppendLine("    {");
            RenderBody(sb, method.State);
            sb.AppendLine("    }");
            sb.AppendLine();
        }

        var generic = session.TypeParameterNames.Count > 0 ? "<" + string.Join(", ", session.TypeParameterNames) + ">" : "";
        var convention = cell.IsVarArg ? "vararg " : "";
        var parameters = string.Join(", ", cell.Arguments.Select((argument, index) =>
            DeclarationType(argument.Type, argument.ExactType) + " " + TypeNameFormatter.IlAsmIdentifier(
                argument.Name ?? "arg" + index.ToString(CultureInfo.InvariantCulture))));
        sb.Append("    .method public static ").Append(convention).Append("object Run").Append(generic).Append('(').Append(parameters).AppendLine(") cil managed");
        sb.AppendLine("    {");
        RenderBody(sb, cell);
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static void RenderBody(StringBuilder sb, CellState state) => RenderBody(sb, state, 2);

    private static void RenderBody(StringBuilder sb, CellState state, int level)
    {
        sb.Append(Pad(level)).Append(".maxstack ")
            .AppendLine(Math.Max(1, state.Analysis.MaxStack).ToString(CultureInfo.InvariantCulture));
        if (state.Locals.Count > 0)
        {
            var locals = state.Locals.Select((l, i) =>
                $"[{i}] {DeclarationType(l.Type, l.ExactType)}{(l.IsPinned ? " pinned" : "")} "
                + TypeNameFormatter.IlAsmIdentifier(l.Name ?? "V_" + i.ToString(CultureInfo.InvariantCulture)));
            sb.Append(Pad(level)).Append(".locals init (").Append(string.Join(", ", locals)).AppendLine(")");
        }

        // A finally or fault written after catch handlers protects the try and those handlers
        // together, as the emitters nest them; the text nests them the same way.
        var nestedTries = new HashSet<int>();
        var nestedTerminals = new HashSet<int>();
        var frames = new Stack<(int TryIndex, bool HasHandler)>();
        for (var i = 0; i < state.Entries.Count; i++)
        {
            var entry = state.Entries[i];
            if (entry.Kind != EntryKind.Block)
            {
                continue;
            }

            switch (entry.Block)
            {
                case BlockKind.Try:
                    frames.Push((i, false));
                    break;
                case BlockKind.Catch:
                case BlockKind.Filter:
                    frames.Push((frames.Pop().TryIndex, true));
                    break;
                case BlockKind.Finally:
                case BlockKind.Fault:
                    if (frames.Peek().HasHandler)
                    {
                        nestedTries.Add(frames.Peek().TryIndex);
                        nestedTerminals.Add(i);
                    }

                    break;
                case BlockKind.End:
                    frames.Pop();
                    break;
                default:
                    break;
            }
        }

        // The transitions the emitters write for the user, leave at the end of a try or a
        // catch and endfinally at the end of a finally, are written out here with a label
        // after each block as their target; the user's own leave stays as typed.
        var indent = level;
        var open = new Stack<(string EndLabel, BlockKind Region)>();
        var ends = 0;
        string NextEnd()
        {
            string label;
            do
            {
                label = "IlReplEnd" + ends.ToString(System.Globalization.CultureInfo.InvariantCulture);
                ends++;
            }
            while (state.DefinedLabels.Contains(label));
            return label;
        }

        void LeaveCurrent()
        {
            var (endLabel, region) = open.Peek();
            if (region is BlockKind.Try or BlockKind.Catch or BlockKind.Filter)
            {
                sb.Append(Pad(indent)).Append("leave ").AppendLine(endLabel);
            }
        }

        for (var index = 0; index < state.Entries.Count; index++)
        {
            var e = state.Entries[index];
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
                            if (nestedTries.Contains(index))
                            {
                                sb.Append(Pad(indent)).AppendLine(".try");
                                sb.Append(Pad(indent)).AppendLine("{");
                                indent++;
                            }

                            sb.Append(Pad(indent)).AppendLine(".try");
                            sb.Append(Pad(indent)).AppendLine("{");
                            indent++;
                            open.Push((NextEnd(), BlockKind.Try));
                            break;
                        case BlockKind.Catch:
                            LeaveCurrent();
                            open.Push((open.Pop().EndLabel, BlockKind.Catch));
                            indent--;
                            sb.Append(Pad(indent)).AppendLine("}");
                            sb.Append(Pad(indent)).Append("catch ").AppendLine(TypeNameFormatter.IlAsmDeclaring(e.CatchType ?? typeof(object)));
                            sb.Append(Pad(indent)).AppendLine("{");
                            indent++;
                            break;
                        case BlockKind.Filter:
                            LeaveCurrent();
                            open.Push((open.Pop().EndLabel, BlockKind.Filter));
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
                            LeaveCurrent();
                            open.Push((open.Pop().EndLabel, BlockKind.Finally));
                            indent--;
                            sb.Append(Pad(indent)).AppendLine("}");
                            if (nestedTerminals.Contains(index))
                            {
                                indent--;
                                sb.Append(Pad(indent)).AppendLine("}");
                            }

                            sb.Append(Pad(indent)).AppendLine("finally");
                            sb.Append(Pad(indent)).AppendLine("{");
                            indent++;
                            break;
                        case BlockKind.Fault:
                            LeaveCurrent();
                            open.Push((open.Pop().EndLabel, BlockKind.Fault));
                            indent--;
                            sb.Append(Pad(indent)).AppendLine("}");
                            if (nestedTerminals.Contains(index))
                            {
                                indent--;
                                sb.Append(Pad(indent)).AppendLine("}");
                            }

                            sb.Append(Pad(indent)).AppendLine("fault");
                            sb.Append(Pad(indent)).AppendLine("{");
                            indent++;
                            break;
                        case BlockKind.End:
                        {
                            var (endLabel, region) = open.Pop();
                            var ending = region is BlockKind.Finally or BlockKind.Fault ? "endfinally" : "leave " + endLabel;
                            sb.Append(Pad(indent)).AppendLine(ending);
                            indent--;
                            sb.Append(Pad(indent)).AppendLine("}");
                            sb.Append(Pad(indent - 1)).Append(endLabel).AppendLine(":");
                            break;
                        }
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
            if (state.Entries.Count > 0 && state.Entries[^1].Kind is EntryKind.Block or EntryKind.Labels)
            {
                sb.Append(Pad(indent)).AppendLine("ldnull");
                sb.Append(Pad(indent)).AppendLine("throw");
            }

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
            OperandKind.Type => name + " " + (instruction.ExactTypeOperand is { } exactType
                ? DeclarationType(exactType)
                : TypeNameFormatter.IlAsm((Type)instruction.Operand!)),
            OperandKind.Method => name + " " + MethodIlAsm((ResolvedMethod)instruction.Operand!),
            OperandKind.Field => name + " " + FieldIlAsm(
                (FieldInfo)instruction.Operand!, instruction.ExactFieldDeclaringType),
            OperandKind.Token => name + " " + instruction.Operand switch
            {
                Type t => instruction.ExactTypeOperand is { } exactType ? DeclarationType(exactType) : TypeNameFormatter.IlAsm(t),
                ResolvedMethod m => "method " + MethodIlAsm(m),
                FieldInfo f => "field " + FieldIlAsm(f, instruction.ExactFieldDeclaringType),
                _ => "?",
            },
            OperandKind.Signature => name + " " + SignatureIlAsm((CalliSignature)instruction.Operand!),
            OperandKind.Labels => name + " (" + string.Join(", ", (string[])instruction.Operand!) + ")",
            OperandKind.Local or OperandKind.Argument => NamedSlot(instruction),
            _ => instruction.Text,
        };
    }

    private static string NamedSlot(Instruction instruction)
    {
        // The user's operand is kept, quoted when it is a name ILAsm would read as a keyword.
        var text = instruction.Text.Trim();
        var space = text.IndexOfAny([' ', '\t']);
        if (space < 0)
        {
            return text;
        }

        var operand = InstructionParser.Unquote(text[(space + 1)..].Trim());
        return text[..space] + " " + (operand.All(char.IsDigit) ? operand : TypeNameFormatter.IlAsmIdentifier(operand));
    }

    private static string Pad(int level) => new(' ', level * 4);

    private static string MethodIlAsm(ResolvedMethod resolved)
    {
        if (resolved.Definition is { } definition)
        {
            return SessionMethodIlAsm(definition, qualify: true);
        }

        if (resolved.Declared is { } declared)
        {
            // A member of a type being written is described by its declaration as written, with
            // the declaring type's and the method's own parameters as !N and !!N; the owner and
            // the method's arguments carry the instantiation.
            var written = resolved.DeclaredDefinition ?? declared;
            var declaredParameters = written.Parameters.Select(SignatureType).ToList();
            if (resolved.OptionalParameterTypes is not null)
            {
                declaredParameters.Add("...");
                declaredParameters.AddRange(resolved.ExactOptionalParameterTypes is { } exactOptional
                    ? exactOptional.Select(SignatureType)
                    : resolved.OptionalParameterTypes.Select(TypeNameFormatter.IlAsm));
            }

            var declaredVarArg = written.CallingConvention.HasFlag(CallingConventions.VarArgs) ? "vararg " : "";
            var declaredArguments = resolved.ExactGenericArguments is { Count: > 0 } exactArguments
                ? "<" + string.Join(", ", exactArguments.Select(SignatureType)) + ">"
                : resolved.GenericArguments is { Count: > 0 } arguments
                    ? "<" + string.Join(", ", arguments.Select(TypeNameFormatter.IlAsm)) + ">"
                : written.TypeParameters.Count == 0 ? "" : "<[" + written.TypeParameters.Count + "]>";
            var declaredName = MemberName(written.Name) + declaredArguments;
            return $"{(written.IsStatic ? "" : "instance ")}{declaredVarArg}"
                + $"{SignatureReturnType(written)} "
                + $"{DeclaringType(resolved)}::{declaredName}"
                + $"({string.Join(", ", declaredParameters)})";
        }

        var method = resolved.Method!;
        var instance = method.IsStatic ? "" : "instance ";
        var vararg = method.CallingConvention.HasFlag(CallingConventions.VarArgs) ? "vararg " : "";
        // The signature is the definition's: a member of an instantiation names the type's
        // parameters as !N, a generic method instance its own as !!N.
        var definitionMethod = DefinitionOf(method);
        var signature = RuntimeSymbolImporter.Import(definitionMethod);
        var metadata = CecilMetadataSignatures.IsRequired(definitionMethod) ? RuntimeMetadataSignatures.Read(definitionMethod) : null;
        var returnType = SignatureType(SignatureSymbolIdentity.AnnotatedReturn(signature));
        if (metadata is not null)
        {
            returnType = IlSignatureRenderer.IlAsm(metadata.ReturnType);
        }

        var name = method is ConstructorInfo ? (method.IsStatic ? ".cctor" : ".ctor") : MemberName(method.Name);
        if (method is MethodInfo g && g.IsGenericMethod)
        {
            name += resolved.ExactGenericArguments is { Count: > 0 } exactArguments
                ? "<" + string.Join(", ", exactArguments.Select(SignatureType)) + ">"
                : "<" + string.Join(", ", g.GetGenericArguments().Select(TypeNameFormatter.IlAsm)) + ">";
        }

        var parameters = signature.Parameters.Select(parameter => SignatureType(SignatureSymbolIdentity.Annotated(parameter))).ToList();
        if (metadata is not null)
        {
            parameters = metadata.Parameters.Select(IlSignatureRenderer.IlAsm).ToList();
        }

        if (resolved.OptionalParameterTypes is not null)
        {
            parameters.Add("...");
            parameters.AddRange(resolved.ExactOptionalParameterTypes is { } exactOptional
                ? exactOptional.Select(SignatureType)
                : resolved.OptionalParameterTypes.Select(TypeNameFormatter.IlAsm));
        }

        var declaring = DeclaringType(resolved);
        return $"{instance}{vararg}{returnType} {declaring}::{name}({string.Join(", ", parameters)})";
    }

    private static string DeclaringType(ResolvedMethod resolved) => resolved.ExactDeclaringType is { } exact
        ? SignatureType(exact)
        : resolved.DeclaringType is null ? "?" : TypeNameFormatter.IlAsmDeclaring(resolved.DeclaringType);

    private static string FieldIlAsm(FieldInfo field, TypeSymbol? exactDeclaringType = null)
    {
        var declaring = exactDeclaringType is null
            ? field.DeclaringType is null ? "?" : TypeNameFormatter.IlAsmDeclaring(field.DeclaringType)
            : SignatureType(exactDeclaringType);
        var definition = DefinitionOf(field);
        var symbol = RuntimeSymbolImporter.Import(definition);
        var exact = RuntimeFieldSignatures.TypeOf(field)
            ?? RuntimeFieldSignatures.TypeOf(definition)
            ?? symbol.ExactType;
        var type = SignatureType(exact ?? symbol.FieldType);
        // A prototype builder has no readable metadata image. Its reflection signature is the declaration
        // supplied by the session, including the element type needed for ordinary pointer fields.
        if (!definition.Module.Assembly.IsDynamic && CecilMetadataSignatures.IsRequired(definition))
        {
            var signature = IlSignatureRenderer.IlAsm(RuntimeMetadataSignatures.Read(definition));
            return $"{signature} {declaring}::{MemberName(field.Name)}";
        }

        try
        {
            // A loaded field carries its modifiers; a builder cannot describe them yet.
            if (exact is null)
            {
                type = Modified(type, definition.GetRequiredCustomModifiers(), definition.GetOptionalCustomModifiers());
            }
        }
        catch (Exception ex) when (ex is NotSupportedException or NotImplementedException)
        {
            // A field of a type being written: its declaration renders the modifiers elsewhere.
        }

        return $"{type} {declaring}::{MemberName(field.Name)}";
    }

    /// <summary>
    /// The method on the generic type definition, or the generic method definition, behind a
    /// member reached through an instantiation; the member itself otherwise.
    /// </summary>
    internal static MethodBase DefinitionOf(MethodBase method)
    {
        var definition = method;
        if (method is MethodInfo { IsGenericMethod: true, IsGenericMethodDefinition: false } generic)
        {
            definition = generic.GetGenericMethodDefinition();
        }

        if (definition.DeclaringType is { IsGenericType: true, IsGenericTypeDefinition: false } owner && owner.GetGenericTypeDefinition() is not System.Reflection.Emit.TypeBuilder)
        {
            try
            {
                definition = definition.Module.ResolveMethod(definition.MetadataToken) ?? definition;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or InvalidOperationException)
            {
                // A wrapper over a builder describes the definition already.
            }
        }

        return definition;
    }

    private static FieldInfo DefinitionOf(FieldInfo field)
    {
        if (field.DeclaringType is { IsGenericType: true, IsGenericTypeDefinition: false } owner && owner.GetGenericTypeDefinition() is not System.Reflection.Emit.TypeBuilder)
        {
            // A wrapper over a builder describes the definition already.
            try
            {
                return field.Module.ResolveField(field.MetadataToken) ?? field;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or InvalidOperationException)
            {
                // A wrapper over a builder describes the definition already.
            }
        }

        return field;
    }

    /// <summary>
    /// A type inside a member reference's signature: generic parameters by position.
    /// </summary>
    private static string SignatureType(TypeSymbol type)
    {
        switch (type.Kind)
        {
            case TypeSymbolKind.Array:
                return SignatureType(type.Element!) + ArraySignatureShape.RenderNative(type.Rank, type.Sizes, type.LowerBounds);
            case TypeSymbolKind.SzArray:
                return SignatureType(type.Element!) + "[]";
            case TypeSymbolKind.ByRef:
                return SignatureType(type.Element!) + "&";
            case TypeSymbolKind.Pointer:
                return SignatureType(type.Element!) + "*";
            case TypeSymbolKind.Modified:
                return ModifiedType(type, SignatureType);
            case TypeSymbolKind.Pinned:
                return SignatureType(type.Element!) + " pinned";
            case TypeSymbolKind.Constructed:
                return (type.IsValueTypeShape ? "valuetype " : "class ")
                    + TypeNameFormatter.IlAsmDeclaring(RuntimeBindingAdapter.Materialize(type.Element!))
                    + "<" + string.Join(", ", type.Arguments.Select(SignatureType)) + ">";
            case TypeSymbolKind.FunctionPointer:
                return FunctionPointerType(type.Signature!, SignatureType);
            default:
                return SignatureType(RuntimeBindingAdapter.Materialize(type));
        }
    }

    private static string DeclarationType(TypeSymbol type)
    {
        switch (type.Kind)
        {
            case TypeSymbolKind.TypeParameter:
                return "!" + TypeNameFormatter.IlAsmIdentifier(type.Name);
            case TypeSymbolKind.MethodParameter:
                return "!!" + TypeNameFormatter.IlAsmIdentifier(type.Name);
            case TypeSymbolKind.Array:
                return DeclarationType(type.Element!) + ArraySignatureShape.RenderNative(
                    type.Rank, type.Sizes, type.LowerBounds);
            case TypeSymbolKind.SzArray:
                return DeclarationType(type.Element!) + "[]";
            case TypeSymbolKind.ByRef:
                return DeclarationType(type.Element!) + "&";
            case TypeSymbolKind.Pointer:
                return DeclarationType(type.Element!) + "*";
            case TypeSymbolKind.Modified:
                return ModifiedType(type, DeclarationType);
            case TypeSymbolKind.Pinned:
                return DeclarationType(type.Element!) + " pinned";
            case TypeSymbolKind.Constructed:
                return (type.IsValueTypeShape ? "valuetype " : "class ")
                    + TypeNameFormatter.IlAsmDeclaring(RuntimeBindingAdapter.Materialize(type.Element!))
                    + "<" + string.Join(", ", type.Arguments.Select(DeclarationType)) + ">";
            case TypeSymbolKind.FunctionPointer:
                return FunctionPointerType(type.Signature!, DeclarationType);
            default:
                return SignatureType(RuntimeBindingAdapter.Materialize(type));
        }
    }

    private static string FunctionPointerType(MethodSignatureSymbol signature, Func<TypeSymbol, string> render)
    {
        var returnType = render(signature.ReturnType);
        var text = SymbolRenderer.Signature(signature, candidate => render(candidate!));
        var end = text.IndexOf(returnType, StringComparison.Ordinal) + returnType.Length;
        return "method " + text[..end] + " *" + text[end..];
    }

    private static string ModifiedType(TypeSymbol type, Func<TypeSymbol, string> render) => render(type.Element!)
        + (type.IsRequired ? " modreq(" : " modopt(")
        + TypeNameFormatter.IlAsmDeclaring(RuntimeBindingAdapter.Materialize(type.Modifier!)) + ")";

    private static string SignatureType(Type type)
    {
        if (type.IsGenericParameter)
        {
            return (type.DeclaringMethod is null ? "!" : "!!") + type.GenericParameterPosition.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (type.IsByRef)
        {
            return SignatureType(type.GetElementType()!) + "&";
        }

        if (type.IsPointer)
        {
            return SignatureType(type.GetElementType()!) + "*";
        }

        if (type.IsArray)
        {
            var element = SignatureType(type.GetElementType()!);
            return type.IsSZArray ? element + "[]" : element + "[" + string.Join(",", Enumerable.Repeat("0...", type.GetArrayRank())) + "]";
        }

        if (type.IsGenericType && !type.IsGenericTypeDefinition)
        {
            var prefix = type.IsValueType ? "valuetype " : "class ";
            return prefix + TypeNameFormatter.IlAsmDeclaring(type.GetGenericTypeDefinition()) + "<" + string.Join(", ", type.GetGenericArguments().Select(SignatureType)) + ">";
        }

        return TypeNameFormatter.IlAsm(type);
    }

    private static string SignatureType(Type type, TypeSymbol? exact) => exact is null
        ? SignatureType(type)
        : SignatureType(exact);

    private static string SignatureType(ArgumentDeclaration parameter) => parameter.ExactType is null
        ? Modified(SignatureType(parameter.Type), parameter.RequiredModifiers, parameter.OptionalModifiers)
        : SignatureType(parameter.ExactType);

    private static string SignatureReturnType(MethodSignature signature) => signature.ExactReturnType is null
        ? Modified(SignatureType(signature.ReturnType), signature.ReturnRequiredModifiers, signature.ReturnOptionalModifiers)
        : SignatureType(signature.ExactReturnType);

    private static string DeclarationType(Type type, TypeSymbol? exact) => exact is null
        ? TypeNameFormatter.IlAsm(type)
        : DeclarationType(exact);

    private static string DeclarationType(ArgumentDeclaration parameter) => parameter.ExactType is null
        ? Modified(DeclarationType(parameter.Type, null), parameter.RequiredModifiers, parameter.OptionalModifiers)
        : DeclarationType(parameter.ExactType);

    private static string DeclarationReturnType(MethodSignature signature) => signature.ExactReturnType is null
        ? Modified(DeclarationType(signature.ReturnType, null), signature.ReturnRequiredModifiers,
            signature.ReturnOptionalModifiers)
        : DeclarationType(signature.ExactReturnType);

    /// <summary>
    /// Renders a session method reference with its complete annotated signature.
    /// </summary>
    internal static string SessionMethodIlAsm(MethodSignature signature, bool qualify = false)
    {
        var parameters = signature.Parameters.Select(SignatureType);
        var owner = qualify ? "IlRepl.Cell::" : "";
        return $"{SignatureReturnType(signature)} {owner}{TypeNameFormatter.IlAsmIdentifier(signature.Name)}"
            + $"({string.Join(", ", parameters)})";
    }

    private static string SignatureIlAsm(CalliSignature signature)
    {
        if (signature.ExactSymbol is { } exact)
        {
            return SymbolRenderer.Signature(exact, type => SignatureType(type!));
        }

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

    private static void NoteDeclaration(
        TypeDeclaration declaration,
        Action<Type?> note,
        Action<TypeSymbol?> noteExact,
        Action<CellState> noteState)
    {
        note(declaration.BaseType);
        foreach (var i in declaration.Interfaces)
        {
            note(i);
        }

        foreach (var parameter in declaration.TypeParameters)
        {
            foreach (var constraint in parameter.Constraints)
            {
                note(constraint);
            }
        }

        foreach (var field in declaration.Fields)
        {
            note(field.Type);
            noteExact(field.ExactType);
            foreach (var modifier in field.RequiredModifiers.Concat(field.OptionalModifiers))
            {
                note(modifier);
            }

            foreach (var attribute in field.CustomAttributes)
            {
                note(attribute.AttributeType);
            }
        }

        foreach (var attribute in declaration.CustomAttributes)
        {
            note(attribute.AttributeType);
        }

        foreach (var property in declaration.Properties)
        {
            note(property.Type);
            noteExact(property.ExactType);
            foreach (var parameterType in property.ParameterTypes)
            {
                note(parameterType);
            }

            foreach (var parameterType in property.ExactParameterTypes)
            {
                noteExact(parameterType);
            }
        }

        foreach (var evt in declaration.Events)
        {
            note(evt.HandlerType);
            noteExact(evt.ExactHandlerType);
        }

        foreach (var over in declaration.Overrides)
        {
            note(over.Target.DeclaringType);
            note(over.BodyReturnType);
            noteExact(over.ExactBodyReturnType);
            foreach (var parameter in over.BodyParameterTypes)
            {
                note(parameter);
            }

            foreach (var parameter in over.ExactBodyParameterTypes)
            {
                noteExact(parameter);
            }
        }

        foreach (var method in declaration.Methods)
        {
            var signature = method.Signature;
            note(signature.ReturnType);
            noteExact(signature.ExactReturnType);
            foreach (var modifier in signature.ReturnRequiredModifiers.Concat(signature.ReturnOptionalModifiers))
            {
                note(modifier);
            }

            foreach (var parameter in signature.Parameters)
            {
                note(parameter.Type);
                noteExact(parameter.ExactType);
                foreach (var modifier in parameter.RequiredModifiers.Concat(parameter.OptionalModifiers))
                {
                    note(modifier);
                }

                foreach (var attribute in parameter.CustomAttributes)
                {
                    note(attribute.AttributeType);
                }
            }

            foreach (var attribute in signature.CustomAttributes)
            {
                note(attribute.AttributeType);
            }

            foreach (var over in method.Overrides)
            {
                note(over.Target.DeclaringType);
            }

            if (method.Body is { } body)
            {
                noteState(body);
            }
        }
    }

    /// <summary>
    /// Renders a type declaration and its nested types as ILAsm.
    /// </summary>
    /// <param name="sb">The output.</param>
    /// <param name="declaration">The declaration.</param>
    /// <param name="level">The indentation level.</param>
    public static void RenderType(StringBuilder sb, TypeDeclaration declaration, int level)
    {
        ArgumentNullException.ThrowIfNull(sb);
        ArgumentNullException.ThrowIfNull(declaration);
        var pad = Pad(level);
        var inner = Pad(level + 1);
        var header = new StringBuilder();
        header.Append(pad).Append(".class ").Append(IlAsmWords.Type(declaration.Attributes, declaration.Kind, declaration.IsNested));
        header.Append(level == 0 && declaration.Namespace.Length > 0 ? string.Join(".", declaration.Namespace.Split('.').Select(TypeNameFormatter.IlAsmIdentifier)) + "." + TypeName(declaration.Name) : TypeName(declaration.Name));
        if (declaration.TypeParameters.Count > 0)
        {
            header.Append('<').Append(string.Join(", ", declaration.TypeParameters.Select(GenericParameterIlAsm))).Append('>');
        }

        if (declaration.BaseType is not null)
        {
            header.Append(" extends ").Append(TypeSpec(declaration.BaseType));
        }

        if (declaration.Interfaces.Count > 0)
        {
            header.Append(" implements ").Append(string.Join(", ", declaration.Interfaces.Select(TypeSpec)));
        }

        sb.AppendLine(header.ToString());
        sb.Append(pad).AppendLine("{");
        foreach (var attribute in declaration.CustomAttributes)
        {
            sb.Append(inner).AppendLine(CustomAttributeIlAsm(attribute));
        }

        if (declaration.PackingSize is { } pack)
        {
            sb.Append(inner).Append(".pack ").AppendLine(pack.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (declaration.ClassSize is { } size)
        {
            sb.Append(inner).Append(".size ").AppendLine(size.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        foreach (var nested in declaration.NestedTypes)
        {
            RenderType(sb, nested, level + 1);
            sb.AppendLine();
        }

        foreach (var field in declaration.Fields)
        {
            sb.Append(inner).AppendLine(FieldIlAsm(field));
            foreach (var attribute in field.CustomAttributes)
            {
                sb.Append(inner).AppendLine(CustomAttributeIlAsm(attribute));
            }
        }

        foreach (var method in declaration.Methods)
        {
            sb.AppendLine();
            RenderMember(sb, method, level + 1);
        }

        foreach (var property in declaration.Properties)
        {
            sb.AppendLine();
            var parameters = property.ParameterTypes.Count == 0 ? "()" : "(" + string.Join(", ",
                property.ParameterTypes.Select((type, index) => property.ExactParameterTypes.ElementAtOrDefault(index) is { } exact
                    ? SignatureType(exact) : TypeNameFormatter.IlAsm(type))) + ")";
            var propertyType = property.ExactType is { } exact
                ? SignatureType(exact) : TypeNameFormatter.IlAsm(property.Type);
            sb.Append(inner).Append(".property ").Append(property.IsStatic ? "" : "instance ")
                .Append(propertyType).Append(' ').Append(TypeNameFormatter.IlAsmIdentifier(property.Name)).AppendLine(parameters);
            sb.Append(inner).AppendLine("{");
            foreach (var attribute in property.CustomAttributes)
            {
                sb.Append(Pad(level + 2)).AppendLine(CustomAttributeIlAsm(attribute));
            }

            if (property.Getter is { } getter)
            {
                sb.Append(Pad(level + 2)).Append(".get ").AppendLine(AccessorIlAsm(declaration, getter));
            }

            if (property.Setter is { } setter)
            {
                sb.Append(Pad(level + 2)).Append(".set ").AppendLine(AccessorIlAsm(declaration, setter));
            }

            foreach (var other in property.Others)
            {
                sb.Append(Pad(level + 2)).Append(".other ").AppendLine(AccessorIlAsm(declaration, other));
            }

            sb.Append(inner).AppendLine("}");
        }

        foreach (var evt in declaration.Events)
        {
            sb.AppendLine();
            var handlerType = evt.ExactHandlerType is null
                ? TypeNameFormatter.IlAsmDeclaring(evt.HandlerType) : SignatureType(evt.ExactHandlerType);
            sb.Append(inner).Append(".event ").Append(handlerType).Append(' ')
                .AppendLine(TypeNameFormatter.IlAsmIdentifier(evt.Name));
            sb.Append(inner).AppendLine("{");
            foreach (var attribute in evt.CustomAttributes)
            {
                sb.Append(Pad(level + 2)).AppendLine(CustomAttributeIlAsm(attribute));
            }

            sb.Append(Pad(level + 2)).Append(".addon ").AppendLine(AccessorIlAsm(declaration, evt.AddOn));
            sb.Append(Pad(level + 2)).Append(".removeon ").AppendLine(AccessorIlAsm(declaration, evt.RemoveOn));
            if (evt.Fire is { } fire)
            {
                sb.Append(Pad(level + 2)).Append(".fire ").AppendLine(AccessorIlAsm(declaration, fire));
            }

            sb.Append(inner).AppendLine("}");
        }

        foreach (var over in declaration.Overrides)
        {
            var returnType = DeclarationType(over.BodyReturnType, over.ExactBodyReturnType);
            var parameters = over.BodyParameterTypes.Select((type, index) =>
                DeclarationType(type, over.ExactBodyParameterType(index)));
            sb.Append(inner).Append(".override ").AppendLine(OverrideTargetIlAsm(over.Target) + " with method "
                + (over.BodyIsStatic ? "" : "instance ") + returnType + " " + TypePath(declaration.FullName) + "::"
                + MemberName(over.BodyName) + "(" + string.Join(", ", parameters) + ")");
        }

        sb.Append(pad).AppendLine("}");
    }

    internal static string GenericParameterIlAsm(GenericParameterDeclaration parameter)
    {
        var words = new List<string>();
        if (parameter.Attributes.HasFlag(GenericParameterAttributes.Covariant))
        {
            words.Add("+");
        }
        else if (parameter.Attributes.HasFlag(GenericParameterAttributes.Contravariant))
        {
            words.Add("-");
        }

        if (parameter.Attributes.HasFlag(GenericParameterAttributes.ReferenceTypeConstraint))
        {
            words.Add("class");
        }

        if (parameter.Attributes.HasFlag(GenericParameterAttributes.NotNullableValueTypeConstraint))
        {
            words.Add("valuetype");
        }

        if (parameter.Attributes.HasFlag(GenericParameterAttributes.DefaultConstructorConstraint))
        {
            words.Add(".ctor");
        }

        if (parameter.Constraints.Count > 0)
        {
            words.Add("(" + string.Join(", ", parameter.Constraints.Select(TypeNameFormatter.IlAsm)) + ")");
        }

        words.Add(TypeNameFormatter.IlAsmIdentifier(parameter.Name));
        return string.Join(" ", words).Replace("+ ", "+", StringComparison.Ordinal).Replace("- ", "-", StringComparison.Ordinal);
    }

    private static string FieldIlAsm(FieldDeclaration field)
    {
        var sb = new StringBuilder(".field ");
        if (field.Offset is { } offset)
        {
            sb.Append('[').Append(offset.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append("] ");
        }

        sb.Append(IlAsmWords.Field(field.Attributes));
        sb.Append(field.ExactType is null
                ? Modified(DeclarationType(field.Type, null), field.RequiredModifiers, field.OptionalModifiers)
                : DeclarationType(field.ExactType))
            .Append(' ').Append(TypeNameFormatter.IlAsmIdentifier(field.Name));
        if (field.HasDefault)
        {
            sb.Append(" = ").Append(ConstantText.IlAsm(field.DefaultValue));
        }

        return sb.ToString();
    }

    private static string Modified(string type, IReadOnlyList<Type> required, IReadOnlyList<Type> optional)
    {
        var text = type;
        foreach (var modifier in optional)
        {
            text += " modopt(" + TypeNameFormatter.IlAsmDeclaring(modifier) + ")";
        }

        foreach (var modifier in required)
        {
            text += " modreq(" + TypeNameFormatter.IlAsmDeclaring(modifier) + ")";
        }

        return text;
    }

    private static void RenderMember(StringBuilder sb, MethodDeclaration method, int level)
    {
        var pad = Pad(level);
        var signature = method.Signature;
        var parameters = string.Join(", ", signature.Parameters.Select((parameter, index) => ParameterIlAsm(parameter, index)));
        var generic = signature.TypeParameters.Count == 0 ? "" : "<" + string.Join(", ", signature.TypeParameters.Select(GenericParameterIlAsm)) + ">";
        var convention = (signature.IsStatic ? "" : "instance ") + (signature.CallingConvention.HasFlag(CallingConventions.VarArgs) ? "vararg " : "");
        var returnType = DeclarationReturnType(signature);
        sb.Append(pad).Append(".method ").Append(IlAsmWords.Method(signature.Attributes)).Append(convention).Append(returnType).Append(' ')
            .Append(MemberName(signature.Name)).Append(generic).Append('(').Append(parameters).Append(") ").AppendLine(IlAsmWords.Implementation(signature.ImplAttributes));
        sb.Append(pad).AppendLine("{");
        var inner = Pad(level + 1);
        foreach (var attribute in signature.CustomAttributes)
        {
            sb.Append(inner).AppendLine(CustomAttributeIlAsm(attribute));
        }

        if (signature.ReturnCustomAttributes.Count > 0)
        {
            sb.Append(inner).AppendLine(".param [0]");
            foreach (var attribute in signature.ReturnCustomAttributes)
            {
                sb.Append(inner).AppendLine(CustomAttributeIlAsm(attribute));
            }
        }

        for (var i = 0; i < signature.Parameters.Count; i++)
        {
            var parameter = signature.Parameters[i];
            if (parameter.HasDefault || parameter.CustomAttributes.Count > 0)
            {
                sb.Append(inner).Append(".param [").Append((i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(']');
                if (parameter.HasDefault)
                {
                    sb.Append(" = ").Append(ConstantText.IlAsm(parameter.DefaultValue));
                }

                sb.AppendLine();
                foreach (var attribute in parameter.CustomAttributes)
                {
                    sb.Append(inner).AppendLine(CustomAttributeIlAsm(attribute));
                }
            }
        }

        foreach (var over in method.Overrides)
        {
            sb.Append(inner).Append(".override ").AppendLine(OverrideTargetIlAsm(over.Target));
        }

        if (method.Body is { } body)
        {
            RenderBody(sb, body, level + 1);
        }

        sb.Append(pad).AppendLine("}");
    }

    private static string ParameterIlAsm(ArgumentDeclaration parameter, int index)
    {
        var words = new StringBuilder();
        if (parameter.Attributes.HasFlag(ParameterAttributes.In))
        {
            words.Append("[in] ");
        }

        if (parameter.Attributes.HasFlag(ParameterAttributes.Out))
        {
            words.Append("[out] ");
        }

        if (parameter.Attributes.HasFlag(ParameterAttributes.Optional))
        {
            words.Append("[opt] ");
        }

        words.Append(DeclarationType(parameter)).Append(' ').Append(TypeNameFormatter.IlAsmIdentifier(
                parameter.Name ?? "arg" + index.ToString(CultureInfo.InvariantCulture)));
        return words.ToString();
    }

    private static string AccessorIlAsm(TypeDeclaration declaration, MethodDeclaration accessor)
    {
        var signature = accessor.Signature;
        var parameters = signature.Parameters.Select(DeclarationType);
        return (signature.IsStatic ? "" : "instance ")
            + DeclarationReturnType(signature) + " "
            + TypePath(declaration.FullName) + "::"
            + MemberName(signature.Name) + "(" + string.Join(", ", parameters) + ")";
    }

    private static string OverrideTargetIlAsm(MethodBase target)
    {
        var declaring = TypeNameFormatter.IlAsmDeclaring(target.DeclaringType!);
        try
        {
            var signature = RuntimeSymbolImporter.Import(target);
            var returnType = target is MethodInfo
                ? SignatureType(SignatureSymbolIdentity.AnnotatedReturn(signature)) : "void";
            var parameters = string.Join(", ", signature.Parameters.Select(parameter =>
                SignatureType(SignatureSymbolIdentity.Annotated(parameter))));
            return $"method {(target.IsStatic ? "" : "instance ")}{returnType} {declaring}::{MemberName(target.Name)}({parameters})";
        }
        catch (NotSupportedException)
        {
            return declaring + "::" + MemberName(target.Name);
        }
    }

    /// <summary>
    /// A member name as ILAsm reads it: the special names stay bare, anything else is quoted when it must be.
    /// </summary>
    internal static string MemberName(string name) => name is ".ctor" or ".cctor" ? name : TypeNameFormatter.IlAsmIdentifier(name);

    /// <summary>
    /// A type name as ILAsm reads it: the arity suffix is part of the name and needs no quotes.
    /// </summary>
    private static string TypeName(string name) => TypeNameFormatter.IlAsmTypeName(name);

    /// <summary>
    /// A nested path as ILAsm reads it, each segment quoted on its own when it must be.
    /// </summary>
    private static string TypePath(string path) => string.Join("/", path.Split('/').Select(TypeName));

    /// <summary>
    /// A base or interface reference: the framework types are spelled out, as ildasm does.
    /// </summary>
    private static string TypeSpec(Type type) => type == typeof(object) ? "[System.Runtime]System.Object" : TypeNameFormatter.IlAsmDeclaring(type);

    private static string CustomAttributeIlAsm(CustomAttributeDeclaration attribute)
    {
        // The line as typed is ILAsm already, in either the blob or the typed form.
        var source = attribute.Source.Trim();
        return source.StartsWith(".custom", StringComparison.Ordinal) ? source : ".custom " + source;
    }
}
