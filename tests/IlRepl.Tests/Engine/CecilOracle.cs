using System.Globalization;
using IlRepl.Engine;
using Mono.Cecil;
using Mono.Cecil.Cil;
using CecilInstruction = Mono.Cecil.Cil.Instruction;
using MethodDefinition = Mono.Cecil.MethodDefinition;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Compares bodies with Mono.Cecil's reader, an implementation that shares nothing with the
/// engine's: instruction by instruction against a disassembly, and body against body across two
/// assemblies by the meaning of every operand.
/// </summary>
internal static class CecilOracle
{
    /// <summary>
    /// Asserts that a disassembly decoded the same instructions and clauses Cecil reads.
    /// </summary>
    /// <param name="cecil">The method as Cecil read it.</param>
    /// <param name="ours">The disassembly.</param>
    public static void AssertSameDecoding(MethodDefinition cecil, DisassembledMethod ours)
    {
        var where = cecil.FullName;
        var instructions = ours.Entries.Where(e => e.Raw is not null).Select(e => e.Raw!).ToList();
        Assert.HasCount(cecil.Body.Instructions.Count, instructions, where + ": instruction count");
        for (var i = 0; i < instructions.Count; i++)
        {
            var expected = cecil.Body.Instructions[i];
            var actual = instructions[i];
            var at = $"{where} at IL_{expected.Offset:x4}";
            Assert.AreEqual(expected.Offset, actual.Offset, at + ": offset");
            Assert.AreEqual(expected.OpCode.Name, actual.Op.Name, at + ": opcode");
            Assert.AreEqual(expected.GetSize(), actual.Size, at + ": size");
            switch (expected.Operand)
            {
                case CecilInstruction target:
                    Assert.AreEqual(target.Offset, actual.BranchTarget, at + ": branch target");
                    break;
                case CecilInstruction[] targets:
                    Assert.AreSequenceEqual(targets.Select(t => t.Offset).ToList(), actual.Operand.SwitchTargets, at + ": switch targets");
                    break;
                case sbyte sb:
                    Assert.AreEqual(sb, actual.Operand.Integer, at + ": int8");
                    break;
                case byte b:
                    Assert.AreEqual(b, actual.Operand.Integer, at + ": uint8");
                    break;
                case int n:
                    Assert.AreEqual(n, actual.Operand.Integer, at + ": int32");
                    break;
                case long l:
                    Assert.AreEqual(l, actual.Operand.Integer, at + ": int64");
                    break;
                case float f:
                    Assert.AreEqual(BitConverter.SingleToUInt32Bits(f), actual.Operand.Bits32, at + ": float32 bits");
                    break;
                case double d:
                    Assert.AreEqual(BitConverter.DoubleToUInt64Bits(d), actual.Operand.Bits64, at + ": float64 bits");
                    break;
                case VariableDefinition v:
                    Assert.AreEqual(v.Index, actual.Operand.Integer, at + ": local index");
                    break;
                case ParameterDefinition p:
                    Assert.AreEqual(ArgumentIndex(cecil, p), actual.Operand.Integer, at + ": argument index");
                    break;
                case string s:
                    Assert.AreEqual(s, ours.Entries.First(e => e.Raw == actual).Instruction?.Operand as string, at + ": string");
                    break;
                case GenericParameter:
                    // Cecil resolves a TypeSpec over a generic parameter to the parameter itself, whose
                    // own token is in the GenericParam table; the IL carries the TypeSpec.
                    Assert.AreEqual(0x1B, actual.Operand.Token >> 24, at + ": generic parameter operand should be a TypeSpec token");
                    break;
                case MemberReference member:
                    Assert.AreEqual(member.MetadataToken.ToInt32(), actual.Operand.Token, at + ": token");
                    break;
                case CallSite site:
                    Assert.AreEqual(site.MetadataToken.ToInt32(), actual.Operand.Token, at + ": signature token");
                    break;
                case null:
                    break;
                default:
                    Assert.Fail(at + ": unexpected Cecil operand " + expected.Operand.GetType().Name);
                    break;
            }
        }

        // Clause order is dispatch order, so it is compared as read, never sorted.
        Assert.HasCount(cecil.Body.ExceptionHandlers.Count, ours.Clauses, where + ": clause count");
        var expectedClauses = cecil.Body.ExceptionHandlers.Select(h => Describe(h, cecil.Body.CodeSize)).ToList();
        var actualClauses = ours.Clauses.Select(Describe).ToList();
        Assert.AreSequenceEqual(expectedClauses, actualClauses, where + ": clauses");
        Assert.AreEqual(cecil.Body.MaxStackSize, ours.MaxStack, where + ": maxstack");
        Assert.AreEqual(cecil.Body.InitLocals, ours.InitLocals, where + ": init locals");
        Assert.HasCount(cecil.Body.Variables.Count, ours.Locals, where + ": local count");
    }

    /// <summary>
    /// Asserts that two bodies mean the same: exact encodings, offsets, immediates, targets, and
    /// clause ranges, and every metadata operand equal by its symbolic identity, with the two
    /// modules' own scopes mapped onto each other.
    /// </summary>
    /// <param name="original">The original method.</param>
    /// <param name="reassembled">The method assembled from the listing.</param>
    /// <param name="originalScope">The full assembly name the original module's own references count as.</param>
    /// <param name="reassembledScope">The full assembly name the reassembled module's own references count as.</param>
    public static void AssertSameMeaning(MethodDefinition original, MethodDefinition reassembled, string originalScope, string reassembledScope) =>
        AssertSameMeaning(original, reassembled, originalScope, reassembledScope, null, null);

    /// <summary>
    /// As above, and with both images at hand also compares the table each token operand comes
    /// from, which Cecil hides: a TypeSpec wrapping a reference is not the reference itself, and
    /// the runtime treats the two differently for an open generic type.
    /// </summary>
    /// <param name="original">The original method.</param>
    /// <param name="reassembled">The method assembled from the listing.</param>
    /// <param name="originalScope">The full assembly name the original module's own references count as.</param>
    /// <param name="reassembledScope">The full assembly name the reassembled module's own references count as.</param>
    /// <param name="originalImage">The original image, or null to skip the token table check.</param>
    /// <param name="reassembledImage">The reassembled image, or null to skip the token table check.</param>
    public static void AssertSameMeaning(MethodDefinition original, MethodDefinition reassembled, string originalScope, string reassembledScope, byte[]? originalImage, byte[]? reassembledImage)
    {
        var where = original.FullName;
        if (originalImage is not null && reassembledImage is not null)
        {
            var a = RawTokens(originalImage, original);
            var b = RawTokens(reassembledImage, reassembled);
            Assert.AreSequenceEqual(a, b, where + ": operand token tables");
        }

        AssertSameMeaningCore(original, reassembled, originalScope, reassembledScope);
    }

    /// <summary>
    /// The shape of every token operand in a body, in order: a type by reference against a type
    /// signature, and a member against a generic method instance. Which table a reference came
    /// through within a shape, a TypeDef or a TypeRef, a MethodDef or a MemberRef, is not part of
    /// it, because a member of the module being reassembled is rightly a reference from outside.
    /// </summary>
    private static List<string> RawTokens(byte[] image, MethodDefinition method)
    {
        using var pe = new System.Reflection.PortableExecutable.PEReader(System.Collections.Immutable.ImmutableArray.Create(image));
        var body = System.Reflection.Metadata.PEReaderExtensions.GetMethodBody(pe, method.RVA);
        var read = IlReader.Read(body.GetILBytes()!);
        Assert.IsEmpty(read.Problems, method.FullName + ": " + string.Join("; ", read.Problems));
        return read.Instructions
            .Where(i => i.Op.OperandType is System.Reflection.Emit.OperandType.InlineType or System.Reflection.Emit.OperandType.InlineTok or System.Reflection.Emit.OperandType.InlineMethod or System.Reflection.Emit.OperandType.InlineField)
            .Select(i => $"IL_{i.Offset:x4} {i.Op.Name} {Shape(i.Operand.Token)}")
            .ToList();
    }

    private static string Shape(int token) => (token >> 24) switch
    {
        0x01 or 0x02 => "type reference",
        0x1B => "type signature",
        0x04 or 0x06 or 0x0A => "member",
        0x2B => "method instance",
        0x11 => "signature",
        var table => $"table 0x{table:x2}",
    };

    private static void AssertSameMeaningCore(MethodDefinition original, MethodDefinition reassembled, string originalScope, string reassembledScope)
    {
        var where = original.FullName;
        var a = original.Body.Instructions;
        var b = reassembled.Body.Instructions;
        Assert.HasCount(a.Count, b, where + ": instruction count\n" + Dump(reassembled));
        for (var i = 0; i < a.Count; i++)
        {
            var at = $"{where} at IL_{a[i].Offset:x4}";
            Assert.AreEqual(a[i].Offset, b[i].Offset, at + ": offset");
            Assert.AreEqual(a[i].OpCode.Value, b[i].OpCode.Value, at + ": opcode encoding");
            Assert.AreEqual(a[i].GetSize(), b[i].GetSize(), at + ": size");
            Assert.AreEqual(Identity(a[i].Operand, original.Module, originalScope), Identity(b[i].Operand, reassembled.Module, reassembledScope), at + ": operand");
        }

        // Clause order is dispatch order: the same clauses in another order mean something else.
        var ha = original.Body.ExceptionHandlers.Select(h => Describe(h, original.Body.CodeSize, original.Module, originalScope)).ToList();
        var hb = reassembled.Body.ExceptionHandlers.Select(h => Describe(h, reassembled.Body.CodeSize, reassembled.Module, reassembledScope)).ToList();
        Assert.AreSequenceEqual(ha, hb, where + ": clauses");
        Assert.AreEqual(original.Body.MaxStackSize, reassembled.Body.MaxStackSize, where + ": maxstack");
        Assert.AreEqual(original.Body.InitLocals, reassembled.Body.InitLocals, where + ": init locals");
        Assert.AreSequenceEqual(
            original.Body.Variables.Select(v => Identity(v.VariableType, original.Module, originalScope)).ToList(),
            reassembled.Body.Variables.Select(v => Identity(v.VariableType, reassembled.Module, reassembledScope)).ToList(),
            where + ": locals");
    }

    private static int ArgumentIndex(MethodDefinition method, ParameterDefinition parameter) => method.Parameters.IndexOf(parameter) + (method.HasThis ? 1 : 0);

    private static string Describe(ExceptionHandler handler, int codeSize) =>
        $"{handler.HandlerType} try {handler.TryStart.Offset}-{handler.TryEnd?.Offset ?? codeSize} filter {handler.FilterStart?.Offset ?? -1} handler {handler.HandlerStart.Offset}-{handler.HandlerEnd?.Offset ?? codeSize} catch {handler.CatchType?.MetadataToken.ToInt32() ?? 0}";

    private static string Describe(IlExceptionClause clause) =>
        $"{clause.Kind} try {clause.TryStart}-{clause.TryEnd} filter {clause.FilterStart ?? -1} handler {clause.HandlerStart}-{clause.HandlerEnd} catch {clause.CatchToken}";

    private static string Describe(ExceptionHandler handler, int codeSize, ModuleDefinition module, string scope) =>
        $"{handler.HandlerType} try {handler.TryStart.Offset}-{handler.TryEnd?.Offset ?? codeSize} filter {handler.FilterStart?.Offset ?? -1} handler {handler.HandlerStart.Offset}-{handler.HandlerEnd?.Offset ?? codeSize} catch {(handler.CatchType is null ? "" : Identity(handler.CatchType, module, scope))}";

    /// <summary>
    /// The symbolic identity of an operand: its full name and the assembly identity of every
    /// scope in it, with the module's own scope named by <paramref name="self"/>. The table a
    /// reference came through is not part of it, so a MethodDef and a MemberRef to one member agree.
    /// </summary>
    private static string Identity(object? operand, ModuleDefinition module, string self)
    {
        switch (operand)
        {
            case null:
                return "";
            case CecilInstruction target:
                return "IL_" + target.Offset.ToString("x4", CultureInfo.InvariantCulture);
            case CecilInstruction[] targets:
                return "(" + string.Join(",", targets.Select(t => t.Offset.ToString("x4", CultureInfo.InvariantCulture))) + ")";
            case float f:
                return "r4:" + BitConverter.SingleToUInt32Bits(f).ToString("x8", CultureInfo.InvariantCulture);
            case double d:
                return "r8:" + BitConverter.DoubleToUInt64Bits(d).ToString("x16", CultureInfo.InvariantCulture);
            case string s:
                return "str:" + s;
            case VariableDefinition v:
                return "V_" + v.Index.ToString(CultureInfo.InvariantCulture);
            case ParameterDefinition p:
                return "A_" + (p.Method is MethodDefinition owner ? ArgumentIndex(owner, p) : p.Sequence).ToString(CultureInfo.InvariantCulture);
            case TypeReference type:
                return TypeIdentity(type, self);
            case FieldReference field:
                return "field " + TypeIdentity(field.FieldType, self) + " " + TypeIdentity(field.DeclaringType, self) + "::" + field.Name;
            case GenericInstanceMethod instance:
                return Identity(instance.ElementMethod, module, self) + "<" + string.Join(",", instance.GenericArguments.Select(g => TypeIdentity(g, self))) + ">";
            case MethodReference method:
                return "method " + Convention(method) + TypeIdentity(method.ReturnType, self) + " " + TypeIdentity(method.DeclaringType, self) + "::" + method.Name + "(" + string.Join(",", method.Parameters.Select(p => TypeIdentity(p.ParameterType, self))) + ")" + (method.HasGenericParameters ? "`" + method.GenericParameters.Count.ToString(CultureInfo.InvariantCulture) : "");
            case CallSite site:
                return "sig " + Convention(site) + TypeIdentity(site.ReturnType, self) + "(" + string.Join(",", site.Parameters.Select(p => TypeIdentity(p.ParameterType, self))) + ")";
            default:
                return operand.GetType().Name + ":" + Convert.ToString(operand, CultureInfo.InvariantCulture);
        }
    }

    private static string Convention(IMethodSignature signature) =>
        (signature.HasThis ? "instance " : "") + (signature.ExplicitThis ? "explicit " : "") + signature.CallingConvention.ToString().ToLowerInvariant() + " ";

    private static string TypeIdentity(TypeReference type, string self)
    {
        switch (type)
        {
            case GenericInstanceType instance:
                return TypeIdentity(instance.ElementType, self) + "<" + string.Join(",", instance.GenericArguments.Select(g => TypeIdentity(g, self))) + ">";
            case ArrayType array:
                return TypeIdentity(array.ElementType, self) + "[" + string.Join(",", array.Dimensions.Select(d => d.ToString())) + "]";
            case ByReferenceType byRef:
                return TypeIdentity(byRef.ElementType, self) + "&";
            case PointerType pointer:
                return TypeIdentity(pointer.ElementType, self) + "*";
            case PinnedType pinned:
                return TypeIdentity(pinned.ElementType, self) + " pinned";
            case RequiredModifierType required:
                return TypeIdentity(required.ElementType, self) + " modreq(" + TypeIdentity(required.ModifierType, self) + ")";
            case OptionalModifierType optional:
                return TypeIdentity(optional.ElementType, self) + " modopt(" + TypeIdentity(optional.ModifierType, self) + ")";
            case SentinelType sentinel:
                return "..., " + TypeIdentity(sentinel.ElementType, self);
            case FunctionPointerType fn:
                return "method " + Convention(fn) + TypeIdentity(fn.ReturnType, self) + " *(" + string.Join(",", fn.Parameters.Select(p => TypeIdentity(p.ParameterType, self))) + ")";
            case GenericParameter parameter:
                return (parameter.Type == GenericParameterType.Method ? "!!" : "!") + parameter.Position.ToString(CultureInfo.InvariantCulture);
            default:
            {
                var name = type.IsNested ? TypeIdentity(type.DeclaringType, self) + "/" + type.Name : (type.Namespace.Length == 0 ? type.Name : type.Namespace + "." + type.Name);
                return type.IsNested ? name : "[" + ScopeName(type, self) + "]" + name;
            }
        }
    }

    /// <summary>
    /// The full identity of the assembly a type lives in: name, version, culture, and public key
    /// token. A compiled reference names a facade such as System.Collections that forwards the
    /// type on, and the listing names where the runtime finds it, so a reference the runtime can
    /// load is followed to the assembly that defines the type; one it cannot keeps the identity
    /// written in the reference, so two versions of one name stay two identities.
    /// </summary>
    private static string ScopeName(TypeReference type, string self)
    {
        switch (type.Scope)
        {
            case AssemblyNameReference assembly:
            {
                var runtime = Type.GetType(type.FullName.Replace('/', '+') + ", " + assembly.FullName, throwOnError: false);
                return runtime is not null ? runtime.Assembly.GetName().FullName : assembly.FullName;
            }

            case ModuleDefinition:
                return self;
            case ModuleReference module:
                return module.Name;
            default:
                return type.Scope?.Name ?? "?";
        }
    }

    private static string Dump(MethodDefinition method) => string.Join("\n", method.Body.Instructions.Select(i => i.ToString()));
}
