using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace IlRepl.Engine;

/// <summary>
/// Reads a method body back into a listing: the bytes through <see cref="IlReader"/>, operands
/// through the module's metadata and its runtime resolution, exception clauses into blocks, and
/// every instruction into the same <see cref="Instruction"/> shape the stack simulator applies.
/// A token that does not resolve becomes a raw line and a note; the listing goes on.
/// </summary>
public static class MethodDisassembler
{
    /// <summary>
    /// Disassembles a method.
    /// </summary>
    /// <param name="requested">The method as the user named it; an instantiation is read through its definition.</param>
    /// <param name="session">The session, for its resolver, its methods, and its types.</param>
    /// <returns>The listing.</returns>
    /// <exception cref="ReplException">The method has no IL, or nothing could read it.</exception>
    public static DisassembledMethod Disassemble(MethodBase requested, Session session)
    {
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(session);
        var notes = new List<string>();
        var definition = IlAsmRenderer.DefinitionOf(requested);
        if (!ReferenceEquals(definition, requested) && definition != requested)
        {
            notes.Add($"showing the definition {MemberResolver.Describe(definition)}; the instantiation shares its body");
        }

        using var body = MethodBodySource.Open(definition, session.Resolver, notes);
        return new Reader(definition, requested, session, body, notes).Read();
    }

    private sealed class Reader
    {
        private readonly MethodBase _definition;
        private readonly MethodBase _requested;
        private readonly Session _session;
        private readonly MethodBodyImage _body;
        private readonly List<string> _notes;
        private readonly Module _module;
        private readonly Type[] _typeArguments;
        private readonly Type[] _methodArguments;
        private readonly GenericContext _generics;
        private readonly MetadataReader? _metadata;
        private readonly MetadataSignatureProvider _provider;

        public Reader(MethodBase definition, MethodBase requested, Session session, MethodBodyImage body, List<string> notes)
        {
            _definition = definition;
            _requested = requested;
            _session = session;
            _body = body;
            _notes = notes;
            _module = definition.Module;
            _typeArguments = definition.DeclaringType is { IsGenericType: true } owner ? owner.GetGenericArguments() : [];
            _methodArguments = definition.IsGenericMethod ? definition.GetGenericArguments() : [];
            _generics = new GenericContext(_typeArguments, _methodArguments);
            _metadata = body.Metadata;
            _provider = new MetadataSignatureProvider(token => Try(() => _module.ResolveType(token, _typeArguments, _methodArguments), null));
        }

        public DisassembledMethod Read()
        {
            var read = IlReader.Read(_body.Il);
            var problems = new List<string>(read.Problems);
            var locals = ReadLocals();
            var unresolvedLocals = locals.Select((l, i) => (l, i)).Where(p => p.l.ToClrType() is null).Select(p => p.i).ToHashSet();
            var arguments = ReadArguments();
            var context = new ParseContext(
                locals.Select(l => new LocalDeclaration(l.ToClrType() ?? typeof(object), null, l.Kind == IlSignatureKind.Pinned)).ToList(),
                arguments,
                _generics,
                _session.Resolver,
                _session.Methods.Select(m => m.Signature).ToList(),
                _session.TypeTable)
            { ThisIndex = _definition.IsStatic ? -1 : 0, Inspecting = true };

            var clauses = ReadClauses();
            var layout = ClauseLayout.Build(clauses, _body.Il.Length);
            foreach (var clause in layout.Fallback)
            {
                _notes.Add("clause not drawn as a block: " + clause.Describe());
            }

            var targets = new HashSet<int>(layout.ReferencedOffsets);
            foreach (var instruction in read.Instructions)
            {
                if (instruction.BranchTarget is int t)
                {
                    targets.Add(t);
                }

                targets.UnionWith(instruction.Operand.SwitchTargets);
            }

            var entries = new List<DisassembledEntry>();
            var boundaryIndex = 0;
            void EmitBoundaries(int upTo)
            {
                while (boundaryIndex < layout.Boundaries.Count && layout.Boundaries[boundaryIndex].Offset <= upTo)
                {
                    var boundary = layout.Boundaries[boundaryIndex++];
                    entries.Add(new DisassembledEntry(DisassembledEntryKind.Block, boundary.Offset)
                    {
                        Block = boundary.Kind,
                        CatchType = boundary.Clause?.CatchType,
                        CatchText = boundary.Kind == BlockKind.Catch ? CatchText(boundary.Clause!) : null,
                    });
                }
            }

            foreach (var instruction in read.Instructions)
            {
                EmitBoundaries(instruction.Offset);
                if (targets.Contains(instruction.Offset))
                {
                    entries.Add(new DisassembledEntry(DisassembledEntryKind.Label, instruction.Offset) { Label = instruction.Label });
                }

                entries.Add(Convert(instruction, locals.Count, unresolvedLocals, arguments.Count));
            }

            EmitBoundaries(_body.Il.Length);
            if (targets.Contains(_body.Il.Length))
            {
                entries.Add(new DisassembledEntry(DisassembledEntryKind.Label, _body.Il.Length) { Label = IlReader.LabelFor(_body.Il.Length) });
            }

            return new DisassembledMethod(_definition, _requested, Header(), _body.MaxStack, _body.InitLocals, locals, context, entries, clauses, _notes, problems, _body.Il.Length, _body.Source);
        }

        private IReadOnlyList<IlSignature> ReadLocals()
        {
            if (_metadata is not null)
            {
                var locals = Try(() => MetadataSignatures.Locals(_metadata, _body.LocalSignatureToken, _provider, _generics), "the local signature");
                if (locals is not null)
                {
                    return locals;
                }
            }

            var reflected = Try(() => _definition.GetMethodBody()?.LocalVariables, "the locals");
            return reflected?.OrderBy(l => l.LocalIndex).Select(l => l.IsPinned ? IlSignature.Pinned(IlSignature.FromType(l.LocalType)) : IlSignature.FromType(l.LocalType)).ToList() ?? [];
        }

        private List<ArgumentDeclaration> ReadArguments()
        {
            var arguments = new List<ArgumentDeclaration>();
            if (!_definition.IsStatic && _definition.DeclaringType is { } declaring)
            {
                arguments.Add(new ArgumentDeclaration(declaring.IsValueType ? declaring.MakeByRefType() : declaring, null, null, ""));
            }

            foreach (var parameter in _definition.GetParameters())
            {
                arguments.Add(new ArgumentDeclaration(parameter.ParameterType, parameter.Name, null, ""));
            }

            return arguments;
        }

        private List<IlExceptionClause> ReadClauses()
        {
            var clauses = new List<IlExceptionClause>();
            foreach (var region in _body.Regions)
            {
                IlSignature? signature = null;
                var catchType = region.CatchType;
                if (region.Kind == IlClauseKind.Catch && region.CatchToken != 0)
                {
                    signature = _metadata is null ? null : Try(() => MetadataSignatures.TypeOperand(_metadata, region.CatchToken, _provider, _generics), $"the catch type at {IlReader.LabelFor(region.HandlerOffset)}");
                    catchType ??= signature?.ToClrType() ?? Try(() => _module.ResolveType(region.CatchToken, _typeArguments, _methodArguments), $"the catch type at {IlReader.LabelFor(region.HandlerOffset)}");
                }
                else if (region.Kind == IlClauseKind.Catch && catchType is not null)
                {
                    signature = IlSignature.FromType(catchType);
                }

                clauses.Add(new IlExceptionClause(
                    region.Kind,
                    region.TryOffset,
                    region.TryOffset + region.TryLength,
                    region.Kind == IlClauseKind.Filter ? region.FilterOffset : null,
                    region.HandlerOffset,
                    region.HandlerOffset + region.HandlerLength,
                    region.CatchToken,
                    signature,
                    catchType));
            }

            return clauses;
        }

        private static string CatchText(IlExceptionClause clause)
        {
            // The block line reads as .show prints it: the short name when the type resolved.
            if (clause.CatchType is { } type)
            {
                return TypeNameFormatter.Pretty(type);
            }

            return clause.CatchSignature is { } signature ? IlSignatureRenderer.Declaring(signature) : $"0x{clause.CatchToken:x8}";
        }

        private string Header()
        {
            var attributes = _definition.Attributes;
            var words = IlAsmWords.Method(attributes);
            var convention = (_definition.IsStatic ? "" : "instance ") + (_definition.CallingConvention.HasFlag(CallingConventions.VarArgs) ? "vararg " : "");
            var name = _definition is ConstructorInfo ? (_definition.IsStatic ? ".cctor" : ".ctor") : IlAsmRenderer.MemberName(_definition.Name);
            var generics = "";
            if (_definition is MethodInfo { IsGenericMethodDefinition: true })
            {
                generics = "<" + string.Join(", ", _methodArguments.Select(p => IlAsmRenderer.GenericParameterIlAsm(new GenericParameterDeclaration(p.Name, p.GenericParameterAttributes, p.GetGenericParameterConstraints())))) + ">";
            }

            IlMethodSignature? signature = _metadata is null ? null : Try(() => MetadataSignatures.MethodDefinition(_metadata, _definition.MetadataToken, _provider, _generics), "the method signature");
            var parameters = _definition.GetParameters();
            string returnType;
            var parameterTexts = new List<string>();
            if (signature is not null && signature.Parameters.Count == parameters.Length)
            {
                returnType = IlSignatureRenderer.IlAsmNamed(signature.ReturnType);
                for (var i = 0; i < parameters.Length; i++)
                {
                    parameterTexts.Add(IlSignatureRenderer.IlAsmNamed(signature.Parameters[i]) + " " + ParameterName(parameters[i], i));
                }
            }
            else
            {
                returnType = _definition is MethodInfo mi
                    ? IlSignatureRenderer.IlAsmNamed(IlSignature.FromType(mi.ReturnType, mi.ReturnParameter.GetRequiredCustomModifiers(), mi.ReturnParameter.GetOptionalCustomModifiers()))
                    : "void";
                for (var i = 0; i < parameters.Length; i++)
                {
                    var p = parameters[i];
                    parameterTexts.Add(IlSignatureRenderer.IlAsmNamed(IlSignature.FromType(p.ParameterType, p.GetRequiredCustomModifiers(), p.GetOptionalCustomModifiers())) + " " + ParameterName(p, i));
                }
            }

            return $".method {words}{convention}{returnType} {name}{generics}({string.Join(", ", parameterTexts)}) {IlAsmWords.Implementation(_definition.GetMethodImplementationFlags())}";
        }

        private static string ParameterName(ParameterInfo parameter, int index) =>
            TypeNameFormatter.IlAsmIdentifier(string.IsNullOrEmpty(parameter.Name) ? "A_" + index.ToString(CultureInfo.InvariantCulture) : parameter.Name);

        private DisassembledEntry Convert(RawInstruction raw, int localCount, HashSet<int> unresolvedLocals, int argumentCount)
        {
            var op = raw.Op;
            var name = op.Name;
            if (op.Emit is not { } emit)
            {
                // no. has no OpCode; its mask prints as ilasm reads it.
                return new DisassembledEntry(DisassembledEntryKind.Raw, raw.Offset) { Text = name + " " + raw.Operand.Integer.ToString(CultureInfo.InvariantCulture), Raw = raw, EffectUnknown = false };
            }

            switch (emit.OperandType)
            {
                case OperandType.InlineNone:
                {
                    int? local = name switch { "ldloc.0" or "stloc.0" => 0, "ldloc.1" or "stloc.1" => 1, "ldloc.2" or "stloc.2" => 2, "ldloc.3" or "stloc.3" => 3, _ => null };
                    int? argument = name switch { "ldarg.0" => 0, "ldarg.1" => 1, "ldarg.2" => 2, "ldarg.3" => 3, _ => null };
                    if (local is int l && l >= localCount)
                    {
                        return Raw(raw, name, $"{name} at {raw.Label} names local {l} but the body declares {localCount}");
                    }

                    if (argument is int a && a >= argumentCount)
                    {
                        return Raw(raw, name, $"{name} at {raw.Label} names argument {a} but the method has {argumentCount}");
                    }

                    var retPops = name == "ret" && _definition is MethodInfo { ReturnType: var rt } && rt != typeof(void) ? 1 : 0;
                    return Instruction(raw, new Instruction { Op = emit, Text = name, LocalIndex = local, ArgumentIndex = argument, RetPops = retPops }, local is int ul && unresolvedLocals.Contains(ul));
                }

                case OperandType.ShortInlineI:
                    return name == "ldc.i4.s"
                        ? Instruction(raw, new Instruction { Op = emit, Text = $"{name} {raw.Operand.Integer}", Kind = OperandKind.SByte, Operand = (sbyte)raw.Operand.Integer })
                        : Instruction(raw, new Instruction { Op = emit, Text = $"{name} {raw.Operand.Integer}", Kind = OperandKind.Byte, Operand = (byte)raw.Operand.Integer });
                case OperandType.InlineI:
                    return Instruction(raw, new Instruction { Op = emit, Text = $"{name} {raw.Operand.Integer}", Kind = OperandKind.Int32, Operand = (int)raw.Operand.Integer });
                case OperandType.InlineI8:
                    return Instruction(raw, new Instruction { Op = emit, Text = $"{name} {raw.Operand.Integer}", Kind = OperandKind.Int64, Operand = raw.Operand.Integer });
                case OperandType.ShortInlineR:
                {
                    var value = BitConverter.Int32BitsToSingle(unchecked((int)raw.Operand.Bits32));
                    return Instruction(raw, new Instruction { Op = emit, Text = $"{name} {FloatText(raw.Operand.Bits32)}", Kind = OperandKind.Single, Operand = value });
                }

                case OperandType.InlineR:
                {
                    var value = BitConverter.Int64BitsToDouble(unchecked((long)raw.Operand.Bits64));
                    return Instruction(raw, new Instruction { Op = emit, Text = $"{name} {DoubleText(raw.Operand.Bits64)}", Kind = OperandKind.Double, Operand = value });
                }

                case OperandType.ShortInlineBrTarget:
                case OperandType.InlineBrTarget:
                {
                    var label = IlReader.LabelFor(raw.BranchTarget!.Value);
                    return Instruction(raw, new Instruction { Op = emit, Text = $"{name} {label}", Kind = OperandKind.Label, Operand = label });
                }

                case OperandType.InlineSwitch:
                {
                    var labels = raw.Operand.SwitchTargets.Select(IlReader.LabelFor).ToArray();
                    return Instruction(raw, new Instruction { Op = emit, Text = $"{name} ({string.Join(", ", labels)})", Kind = OperandKind.Labels, Operand = labels });
                }

                case OperandType.ShortInlineVar:
                case OperandType.InlineVar:
                {
                    var index = (int)raw.Operand.Integer;
                    var isLocal = name.StartsWith("ldloc", StringComparison.Ordinal) || name.StartsWith("stloc", StringComparison.Ordinal);
                    if (isLocal)
                    {
                        if (index >= localCount)
                        {
                            return Raw(raw, $"{name} {index}", $"{name} at {raw.Label} names local {index} but the body declares {localCount}");
                        }

                        return Instruction(raw, new Instruction { Op = emit, Text = $"{name} V_{index}", Kind = OperandKind.Local, Operand = index, LocalIndex = index }, unresolvedLocals.Contains(index));
                    }

                    if (index >= argumentCount)
                    {
                        return Raw(raw, $"{name} {index}", $"{name} at {raw.Label} names argument {index} but the method has {argumentCount}");
                    }

                    return Instruction(raw, new Instruction { Op = emit, Text = $"{name} {index}", Kind = OperandKind.Argument, Operand = index, ArgumentIndex = index });
                }

                case OperandType.InlineString:
                {
                    var value = Try(() => _module.ResolveString(raw.Operand.Token), $"the string at {raw.Label}");
                    return value is null
                        ? Raw(raw, $"{name} 0x{raw.Operand.Token:x8}", null)
                        : Instruction(raw, new Instruction { Op = emit, Text = $"{name} {LiteralParser.Escape(value)}", Kind = OperandKind.String, Operand = value });
                }

                case OperandType.InlineType:
                    return TypeInstruction(raw, emit);
                case OperandType.InlineField:
                    return FieldInstruction(raw, emit);
                case OperandType.InlineMethod:
                    return MethodInstruction(raw, emit);
                case OperandType.InlineTok:
                    return TokenInstruction(raw, emit);
                case OperandType.InlineSig:
                    return SignatureInstruction(raw, emit);
                default:
                    return Raw(raw, name, $"{name} at {raw.Label} has an operand layout the reader does not know");
            }
        }

        private DisassembledEntry TypeInstruction(RawInstruction raw, OpCode emit)
        {
            var token = raw.Operand.Token;
            var signature = _metadata is null ? null : Try(() => MetadataSignatures.TypeOperand(_metadata, token, _provider, _generics), $"the type at {raw.Label}");
            var type = signature?.ToClrType() ?? Try(() => _module.ResolveType(token, _typeArguments, _methodArguments), signature is null ? $"the type at {raw.Label}" : null);
            var text = signature is not null ? IlSignatureRenderer.TypeOperand(signature, IsTypeSpecification(token)) : type is not null ? TypeOperandText(type) : $"0x{token:x8}";
            if (type is null && signature is null)
            {
                return Raw(raw, $"{emit.Name} {text}", null);
            }

            return Instruction(raw, new Instruction { Op = emit, Text = $"{emit.Name} {text}", Kind = OperandKind.Type, Operand = type }, type is null);
        }

        private DisassembledEntry FieldInstruction(RawInstruction raw, OpCode emit)
        {
            var token = raw.Operand.Token;
            var field = Try(() => _module.ResolveField(token, _typeArguments, _methodArguments), $"the field at {raw.Label}");
            var text = FieldText(token, field);
            if (field is null)
            {
                return Raw(raw, $"{emit.Name} {text ?? $"0x{token:x8}"}", null);
            }

            text ??= IlAsmRenderer.RenderInstruction(new Instruction { Op = emit, Text = "", Kind = OperandKind.Field, Operand = field })[(emit.Name!.Length + 1)..];
            return Instruction(raw, new Instruction { Op = emit, Text = $"{emit.Name} {text}", Kind = OperandKind.Field, Operand = field });
        }

        private DisassembledEntry MethodInstruction(RawInstruction raw, OpCode emit)
        {
            var token = raw.Operand.Token;
            var method = Try(() => _module.ResolveMethod(token, _typeArguments, _methodArguments), $"the method at {raw.Label}");
            var (resolved, text, effectUnknown) = MethodOperand(token, method, raw.Label);
            if (resolved is null)
            {
                return Raw(raw, $"{emit.Name} {text ?? $"0x{token:x8}"}", null);
            }

            text ??= IlAsmRenderer.RenderInstruction(new Instruction { Op = emit, Text = "", Kind = OperandKind.Method, Operand = resolved })[(emit.Name!.Length + 1)..];
            return Instruction(raw, new Instruction { Op = emit, Text = $"{emit.Name} {text}", Kind = OperandKind.Method, Operand = resolved }, effectUnknown);
        }

        private DisassembledEntry TokenInstruction(RawInstruction raw, OpCode emit)
        {
            var token = raw.Operand.Token;
            var kind = TokenKind(token, raw.Label);

            var member = Try(() => _module.ResolveMember(token, _typeArguments, _methodArguments), $"the token at {raw.Label}");
            switch (member)
            {
                case Type type:
                {
                    var signature = _metadata is null ? null : Try(() => MetadataSignatures.TypeOperand(_metadata, token, _provider, _generics), null);
                    var text = signature is not null ? IlSignatureRenderer.TypeOperand(signature, IsTypeSpecification(token)) : TypeOperandText(type);
                    return Instruction(raw, new Instruction { Op = emit, Text = $"{emit.Name} {text}", Kind = OperandKind.Token, Operand = type });
                }

                case FieldInfo field:
                    return Instruction(raw, new Instruction { Op = emit, Text = $"{emit.Name} field {FieldText(token, field) ?? IlAsmRenderer.RenderInstruction(new Instruction { Op = OpCodes.Ldsfld, Text = "", Kind = OperandKind.Field, Operand = field })[7..]}", Kind = OperandKind.Token, Operand = field });
                case MethodBase method:
                {
                    var (resolved, text, _) = MethodOperand(token, method, raw.Label);
                    return Instruction(raw, new Instruction { Op = emit, Text = $"{emit.Name} method {text ?? IlAsmRenderer.RenderInstruction(new Instruction { Op = OpCodes.Call, Text = "", Kind = OperandKind.Method, Operand = resolved! })[5..]}", Kind = OperandKind.Token, Operand = resolved });
                }

                default:
                {
                    // Unresolved: print what the row says.
                    string? text = null;
                    if (_metadata is not null && kind is not null)
                    {
                        text = kind switch
                        {
                            HandleKind.TypeDefinition or HandleKind.TypeReference or HandleKind.TypeSpecification => Try(() => MetadataSignatures.TypeOperand(_metadata, token, _provider, _generics), null) is { } sig ? IlSignatureRenderer.IlAsm(sig) : null,
                            HandleKind.FieldDefinition => FieldText(token, null) is { } fieldText ? "field " + fieldText : null,
                            HandleKind.MethodDefinition or HandleKind.MethodSpecification => MethodOperand(token, null, raw.Label).Text is { } methodText ? "method " + methodText : null,
                            _ => null,
                        };
                    }

                    return Raw(raw, $"{emit.Name} {text ?? $"0x{token:x8}"}", null);
                }
            }
        }

        private DisassembledEntry SignatureInstruction(RawInstruction raw, OpCode emit)
        {
            var token = raw.Operand.Token;
            var signature = _metadata is null ? null : Try(() => MetadataSignatures.StandaloneMethod(_metadata, token, _provider, _generics), $"the signature at {raw.Label}");
            if (signature is null)
            {
                return Raw(raw, $"{emit.Name} 0x{token:x8}", _metadata is null ? $"the signature at {raw.Label} needs the module's metadata, which this assembly does not expose" : null);
            }

            var calli = signature.ToCalliSignature();
            var text = $"{emit.Name} {IlSignatureRenderer.IlAsm(signature)}";
            return calli is null
                ? new DisassembledEntry(DisassembledEntryKind.Instruction, raw.Offset) { Instruction = new Instruction { Op = emit, Text = text, Kind = OperandKind.Signature }, Raw = raw, EffectUnknown = true }
                : Instruction(raw, new Instruction { Op = emit, Text = text, Kind = OperandKind.Signature, Operand = calli });
        }

        private static bool IsTypeSpecification(int token) => (token >> 24) == 0x1B;

        /// <summary>
        /// A type operand spelled from a runtime type, for a module without metadata: bare for a
        /// plain type, as the compilers encode it.
        /// </summary>
        private static string TypeOperandText(Type type) =>
            type.IsGenericType || type.HasElementType || type.IsGenericParameter || TypeNameFormatter.IsFunctionPointer(type) ? TypeNameFormatter.IlAsm(type) : TypeNameFormatter.IlAsmDeclaring(type);

        /// <summary>
        /// What table a token names, with a member reference classified as a method or a field;
        /// null when there is no metadata or the token names no row. A damaged token is a note,
        /// not a failure, like every other operand that does not resolve.
        /// </summary>
        private HandleKind? TokenKind(int token, string label)
        {
            if (_metadata is null)
            {
                return null;
            }

            try
            {
                var handle = MetadataTokens.EntityHandle(token);
                if (handle.Kind != HandleKind.MemberReference)
                {
                    return handle.Kind;
                }

                return _metadata.GetMemberReference((MemberReferenceHandle)handle).GetKind() == MemberReferenceKind.Method ? HandleKind.MethodDefinition : HandleKind.FieldDefinition;
            }
            catch (Exception ex) when (ex is BadImageFormatException or ArgumentException or InvalidOperationException)
            {
                _notes.Add($"the token at {label} names no row: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// The operand and text for a method token: a session method by its bare name, otherwise
        /// the member reference as the metadata spells it, with the runtime member for the simulator.
        /// </summary>
        private (ResolvedMethod? Resolved, string? Text, bool EffectUnknown) MethodOperand(int token, MethodBase? method, string label)
        {
            if (method is not null && SessionAssemblies.TryGetDefinition(method.Module.Assembly, out var owner) && owner.Kind is SessionAssemblyKind.Trampoline or SessionAssemblyKind.Methods)
            {
                var sessionMethod = _session.Methods.FirstOrDefault(m => m.Trampoline.Method == method || m.Version.Body == method)
                    ?? _session.Methods.FirstOrDefault(m => m.Signature.Name == method.Name);
                if (sessionMethod is not null)
                {
                    var signature = sessionMethod.Signature;
                    var text = $"{TypeNameFormatter.IlAsm(signature.ReturnType)} {TypeNameFormatter.IlAsmIdentifier(signature.Name)}({string.Join(", ", signature.ParameterTypes.Select(TypeNameFormatter.IlAsm))})";
                    return (new ResolvedMethod(signature), text, false);
                }
            }

            IlMethodSignature? memberSignature = null;
            IReadOnlyList<IlSignature>? instantiation = null;
            string? memberText = null;
            if (_metadata is not null)
            {
                memberSignature = Try(() => MetadataSignatures.MethodOperand(_metadata, token, _provider, _generics, out instantiation), method is null ? $"the method at {label}" : null);
                if (memberSignature is not null)
                {
                    var (declaring, name) = MemberRow(token, method);
                    memberText = IlSignatureRenderer.MemberReference(memberSignature, declaring, name, instantiation);
                }
            }

            if (method is null)
            {
                return (null, memberText, true);
            }

            Type[]? optional = null;
            var effectUnknown = false;
            if (memberSignature?.OptionalParameters is { } optionalSignatures)
            {
                var projected = optionalSignatures.Select(p => p.ToClrType()).ToArray();
                if (projected.Any(p => p is null))
                {
                    effectUnknown = true;
                }
                else
                {
                    optional = projected!;
                }
            }
            else if (method.CallingConvention.HasFlag(CallingConventions.VarArgs) && _metadata is null)
            {
                _notes.Add($"the vararg call at {label} names optional argument types this assembly's metadata cannot be read for; the stack after it is unknown");
                effectUnknown = true;
            }

            return (new ResolvedMethod(method, optional), memberText, effectUnknown);
        }

        private string? FieldText(int token, FieldInfo? field)
        {
            if (_metadata is null)
            {
                return null;
            }

            var signature = Try(() => MetadataSignatures.FieldOperand(_metadata, token, _provider, _generics), field is null ? $"the field at {IlReader.LabelFor(0)}" : null);
            if (signature is null)
            {
                return null;
            }

            var (declaring, name) = MemberRow(token, field);
            return $"{IlSignatureRenderer.IlAsm(signature)} {declaring}::{name}";
        }

        /// <summary>
        /// The declaring type and the name of a member token, spelled for a member position.
        /// </summary>
        private (string Declaring, string Name) MemberRow(int token, MemberInfo? resolved)
        {
            try
            {
                return MemberRowCore(token, resolved);
            }
            catch (Exception ex) when (ex is BadImageFormatException or ArgumentException or InvalidOperationException)
            {
                // A token whose row cannot be read: the runtime member, when there is one, still names it.
                return (resolved?.DeclaringType is { } declaring ? TypeNameFormatter.IlAsmDeclaring(declaring) : "?", resolved is null ? "?" : IlAsmRenderer.MemberName(resolved.Name));
            }
        }

        private (string Declaring, string Name) MemberRowCore(int token, MemberInfo? resolved)
        {
            var handle = MetadataTokens.EntityHandle(token);
            string name;
            IlSignature? parent = null;
            switch (handle.Kind)
            {
                case HandleKind.MethodDefinition:
                {
                    var row = _metadata!.GetMethodDefinition((MethodDefinitionHandle)handle);
                    name = _metadata.GetString(row.Name);
                    parent = _provider.GetTypeFromDefinition(_metadata, row.GetDeclaringType(), 0);
                    break;
                }

                case HandleKind.FieldDefinition:
                {
                    var row = _metadata!.GetFieldDefinition((FieldDefinitionHandle)handle);
                    name = _metadata.GetString(row.Name);
                    parent = _provider.GetTypeFromDefinition(_metadata, row.GetDeclaringType(), 0);
                    break;
                }

                case HandleKind.MemberReference:
                {
                    var row = _metadata!.GetMemberReference((MemberReferenceHandle)handle);
                    name = _metadata.GetString(row.Name);
                    parent = row.Parent.Kind switch
                    {
                        HandleKind.TypeDefinition or HandleKind.TypeReference or HandleKind.TypeSpecification => Try(() => MetadataSignatures.TypeOperand(_metadata, MetadataTokens.GetToken(row.Parent), _provider, _generics), null),
                        HandleKind.MethodDefinition => _provider.GetTypeFromDefinition(_metadata, _metadata.GetMethodDefinition((MethodDefinitionHandle)row.Parent).GetDeclaringType(), 0),
                        _ => null,
                    };
                    break;
                }

                case HandleKind.MethodSpecification:
                    return MemberRowCore(MetadataTokens.GetToken(_metadata!.GetMethodSpecification((MethodSpecificationHandle)handle).Method), resolved);
                default:
                    name = resolved?.Name ?? "?";
                    break;
            }

            var memberName = name is ".ctor" or ".cctor" ? name : IlAsmRenderer.MemberName(name);

            // The row says whether the owner is an open definition or an instantiation; reflection
            // spells a definition with its own parameters, which are out of scope in the listing.
            if (parent is { HasUnresolved: false })
            {
                return (IlSignatureRenderer.Declaring(parent), memberName);
            }

            if (resolved?.DeclaringType is { } declaringType)
            {
                return (declaringType.IsGenericTypeDefinition ? IlSignatureRenderer.Declaring(IlSignature.Named(declaringType)) : TypeNameFormatter.IlAsmDeclaring(declaringType), memberName);
            }

            return (parent is null ? "?" : IlSignatureRenderer.Declaring(parent), memberName);
        }

        private static DisassembledEntry Instruction(RawInstruction raw, Instruction instruction, bool effectUnknown = false) =>
            new(DisassembledEntryKind.Instruction, raw.Offset) { Instruction = instruction, Raw = raw, EffectUnknown = effectUnknown };

        private DisassembledEntry Raw(RawInstruction raw, string text, string? note)
        {
            if (note is not null)
            {
                _notes.Add(note);
            }

            return new DisassembledEntry(DisassembledEntryKind.Raw, raw.Offset) { Text = text, Raw = raw, EffectUnknown = true };
        }

        private T? Try<T>(Func<T?> resolve, string? what)
            where T : class
        {
            try
            {
                return resolve();
            }
            catch (Exception ex) when (ex is ArgumentException or MissingMemberException or NotSupportedException or FileNotFoundException
                or FileLoadException or TypeLoadException or BadImageFormatException or InvalidOperationException or NotImplementedException)
            {
                if (what is not null)
                {
                    _notes.Add($"{what} could not be resolved: {ex.Message}");
                }

                return null;
            }
        }

        private static string FloatText(uint bits)
        {
            // A finite value prints as the shortest decimal that reads back to the same bits; NaN
            // and the infinities print as their bits, which both the parser and ilasm accept.
            var value = BitConverter.Int32BitsToSingle(unchecked((int)bits));
            var text = value.ToString("R", CultureInfo.InvariantCulture);
            if (float.IsFinite(value) && float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var back) && BitConverter.SingleToInt32Bits(back) == unchecked((int)bits))
            {
                return text.Contains('.') || text.Contains('E') ? text : text + ".0";
            }

            return $"float32(0x{bits:x8})";
        }

        private static string DoubleText(ulong bits)
        {
            var value = BitConverter.Int64BitsToDouble(unchecked((long)bits));
            var text = value.ToString("R", CultureInfo.InvariantCulture);
            if (double.IsFinite(value) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var back) && BitConverter.DoubleToInt64Bits(back) == unchecked((long)bits))
            {
                return text.Contains('.') || text.Contains('E') ? text : text + ".0";
            }

            return $"float64(0x{bits:x16})";
        }
    }
}
