using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using IlRepl.Engine;
using Mono.Cecil;
using MethodDefinition = Mono.Cecil.MethodDefinition;
using OpCodes = Mono.Cecil.Cil.OpCodes;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Independent checks of the disassembly: Mono.Cecil's reader over the same bytes, ildasm's text
/// over the same assembly, and a round trip through ilasm compared by meaning.
/// </summary>
[TestClass]
public sealed partial class DisassemblyFidelityTests
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;

    private static (Session Session, Assembly Assembly, ModuleDefinition Module) Fixtures()
    {
        var session = new Session();
        var assembly = session.Resolver.Load(SampleHost.Samples.FixturesDll);
        var module = ModuleDefinition.ReadModule(SampleHost.Samples.FixturesDll);
        return (session, assembly, module);
    }

    private static IEnumerable<(MethodDefinition Cecil, MethodBase Runtime)> Bodies(Assembly assembly, ModuleDefinition module)
    {
        foreach (var type in module.GetTypes().Where(t => t.Methods.Any(m => m.HasBody)))
        {
            var runtimeType = assembly.GetType(type.FullName.Replace('/', '+'), throwOnError: true)!;
            var members = runtimeType.GetMethods(All).Cast<MethodBase>().Concat(runtimeType.GetConstructors(All)).ToList();
            foreach (var method in type.Methods.Where(m => m.HasBody))
            {
                yield return (method, members.First(m => m.MetadataToken == method.MetadataToken.ToInt32()));
            }
        }
    }

    /// <summary>
    /// Every compiled C# body decodes to the instructions and clauses Cecil reads.
    /// </summary>
    [TestMethod]
    public void Reader_MatchesCecil_ForCompiledCSharp()
    {
        var (session, assembly, module) = Fixtures();
        var count = 0;
        foreach (var (cecil, runtime) in Bodies(assembly, module))
        {
            CecilOracle.AssertSameDecoding(cecil, MethodDisassembler.Disassemble(runtime, session));
            count++;
        }

        Assert.IsGreaterThan(15, count, "the fixture library should hold every shape the tests want");
    }

    /// <summary>
    /// Shapes C# does not produce decode the same way: a switch, calli, every ldtoken form, both float widths, and no.
    /// </summary>
    [TestMethod]
    public void Reader_MatchesCecil_ForHandWrittenShapes()
    {
        var session = new Session();
        var (assembly, image, fixture) = CecilFixture.Build((module, type) =>
        {
            type.Fields.Add(new Mono.Cecil.FieldDefinition("F", Mono.Cecil.FieldAttributes.Public | Mono.Cecil.FieldAttributes.Static, module.TypeSystem.Int32));
            var target = new MethodDefinition("Target", Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Static, module.TypeSystem.Int32);
            target.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int32));
            target.Body.GetILProcessor().Emit(OpCodes.Ldarg_0);
            target.Body.GetILProcessor().Emit(OpCodes.Ret);
            type.Methods.Add(target);
            var m = new MethodDefinition("M", Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Static, module.TypeSystem.Void);
            m.Parameters.Add(new ParameterDefinition("x", Mono.Cecil.ParameterAttributes.None, module.TypeSystem.Int32));
            type.Methods.Add(m);
            var il = m.Body.GetILProcessor();
            var a = il.Create(OpCodes.Nop);
            var b = il.Create(OpCodes.Nop);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Switch, new[] { a, b });
            il.Append(a);
            il.Append(b);
            il.Emit(OpCodes.Ldc_R4, BitConverter.Int32BitsToSingle(0x7F800001));
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ldc_R8, -0.0);
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ldc_I8, long.MinValue);
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ldtoken, module.TypeSystem.Int32);
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ldtoken, type.Fields[0]);
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ldtoken, target);
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ldc_I4_5);
            il.Emit(OpCodes.Ldftn, target);
            var site = new CallSite(module.TypeSystem.Int32);
            site.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int32));
            il.Emit(OpCodes.Calli, site);
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.No, (byte)1);
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ret);
        }, session.Resolver);
        var module = ModuleDefinition.ReadModule(new MemoryStream(image));
        var cecil = module.Types.First(t => t.Name == "Fixture").Methods.First(m => m.Name == "M");
        var ours = MethodDisassembler.Disassemble(fixture.GetMethod("M")!, session);
        CecilOracle.AssertSameDecoding(cecil, ours);
        Assert.Contains("no. 1", DisassemblyText.Instructions(ours));
        Assert.IsEmpty(ours.Problems);
    }

    /// <summary>
    /// ildasm prints the same offsets, opcodes, branch targets, maxstack, init bit, and clause
    /// structure for every compiled body. Operand spelling is the REPL's own, so it is not compared.
    /// </summary>
    [TestMethod]
    public void Listing_MatchesIldasm_ForCompiledCSharp()
    {
        var text = IldasmLocator.Disassemble(SampleHost.Samples.FixturesDll);
        var expected = ParseIldasm(text);
        var (session, assembly, module) = Fixtures();
        var compared = 0;
        foreach (var (cecil, runtime) in Bodies(assembly, module))
        {
            var key = cecil.DeclaringType.FullName + "::" + cecil.Name + "/" + cecil.Parameters.Count.ToString(CultureInfo.InvariantCulture);
            Assert.IsTrue(expected.TryGetValue(key, out var ildasm), "ildasm output lacks " + key + "\n" + string.Join("\n", expected.Keys));
            var ours = MethodDisassembler.Disassemble(runtime, session);
            var instructions = ours.Entries.Where(e => e.Raw is not null).ToList();
            Assert.HasCount(ildasm.Instructions.Count, instructions, key + ": instruction count");
            for (var i = 0; i < instructions.Count; i++)
            {
                var (offset, opcode, operand) = ildasm.Instructions[i];
                Assert.AreEqual(offset, instructions[i].Offset, key + ": offset");
                Assert.AreEqual(opcode, instructions[i].Raw!.Op.Name, key + $" at IL_{offset:x4}: opcode");
                if (instructions[i].Raw!.BranchTarget is int target)
                {
                    Assert.AreEqual(IlReader.LabelFor(target).ToUpperInvariant(), operand.ToUpperInvariant(), key + $" at IL_{offset:x4}: target");
                }

                if (instructions[i].Raw!.Operand.SwitchTargets.Length > 0)
                {
                    var targets = TargetList().Matches(operand).Select(m => m.Value.ToUpperInvariant()).ToList();
                    Assert.AreSequenceEqual(instructions[i].Raw!.Operand.SwitchTargets.Select(t => IlReader.LabelFor(t).ToUpperInvariant()).ToList(), targets, key + $" at IL_{offset:x4}: switch targets");
                }
            }

            Assert.AreEqual(ildasm.MaxStack, ours.MaxStack, key + ": maxstack");
            Assert.AreEqual(ildasm.InitLocals, ours.InitLocals, key + ": init locals");
            var native = IlAsmClauseWriter.Write(ours).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).SelectMany(ClauseWords).ToList();
            Assert.AreSequenceEqual(ildasm.ClauseWords, native, key + ": clause structure");
            compared++;
        }

        Assert.IsGreaterThan(15, compared);
    }

    /// <summary>
    /// Every compiled body assembles back through ilasm inside a scaffold that keeps its owning
    /// type and signature, and the reassembled body means the same. The try, catch, and finally
    /// fixture runs and returns eleven on its exception path.
    /// </summary>
    [TestMethod]
    public void Scaffold_RoundTrips_ThroughIlasm()
    {
        _ = IlasmLocator.Require();
        var (session, assembly, module) = Fixtures();
        var guarded = default(byte[]);
        var count = 0;
        foreach (var (cecil, runtime) in Bodies(assembly, module))
        {
            var ours = MethodDisassembler.Disassemble(runtime, session);
            var source = Scaffold(ours);
            var image = IlasmLocator.Assemble(source);
            var reassembled = ModuleDefinition.ReadModule(new MemoryStream(image));
            var method = reassembled.Types.First(t => t.Name == "T").Methods.Single(m => m.HasBody);
            CecilOracle.AssertSameMeaning(cecil, method, "Fixtures", "Fixtures");
            if (cecil.Name == "Guarded")
            {
                guarded = image;
            }

            count++;
        }

        Assert.IsGreaterThan(15, count);
        Assert.IsNotNull(guarded);
        var context = new System.Runtime.Loader.AssemblyLoadContext("ilasm-guarded", isCollectible: true);
        var loaded = context.LoadFromStream(new MemoryStream(guarded));
        var run = loaded.GetType("N.T")!.GetMethod("Guarded")!;
        Assert.AreEqual(11, run.Invoke(null, [true]));
        Assert.AreEqual(2, run.Invoke(null, [false]));
        context.Unload();
    }

    /// <summary>
    /// A body whose handlers sit in a different order from their clauses reassembles with the same
    /// dispatch: the offset form keeps the metadata order, and the Exception handler still wins.
    /// </summary>
    [TestMethod]
    public void Scaffold_KeepsHandlerOrder_WhenLexicalOrderDiffers()
    {
        _ = IlasmLocator.Require();
        var session = new Session();
        var (_, image, fixture) = CecilFixture.Build(MethodDisassemblerTests.AddOutOfOrderHandlers, session.Resolver);
        Assert.AreEqual(2, fixture.GetMethod("M")!.Invoke(null, null), "the original dispatches to the Exception handler first");
        var listing = MethodDisassembler.Disassemble(fixture.GetMethod("M")!, session);
        var reassembled = IlasmLocator.Assemble(Scaffold(listing));
        var original = ModuleDefinition.ReadModule(new MemoryStream(image)).Types.First(t => t.Name == "Fixture").Methods.First(m => m.Name == "M");
        var method = ModuleDefinition.ReadModule(new MemoryStream(reassembled)).Types.First(t => t.Name == "T").Methods.Single(m => m.HasBody);
        CecilOracle.AssertSameMeaning(original, method, fixture.Assembly.GetName().Name!, fixture.Assembly.GetName().Name!);
        var context = new System.Runtime.Loader.AssemblyLoadContext("ilasm-order", isCollectible: true);
        var loaded = context.LoadFromStream(new MemoryStream(reassembled));
        Assert.AreEqual(2, loaded.GetType("N.T")!.GetMethod("M")!.Invoke(null, null), "the reassembled body dispatches the same way");
        context.Unload();
    }

    private static string Scaffold(DisassembledMethod method)
    {
        var self = method.Method.Module.Assembly.GetName().Name!;
        var body = IlAsmClauseWriter.Write(method);
        var locals = method.Locals.Count == 0
            ? ""
            : $"    {(method.InitLocals ? ".locals init (" : ".locals (")}{string.Join(", ", method.Locals.Select((l, i) => IlSignatureRenderer.IlAsmNamed(l) + " V_" + i.ToString(CultureInfo.InvariantCulture)))})\n";
        var sb = new StringBuilder();
        var externs = AssemblyHint().Matches(method.Header + body + locals).Select(m => m.Groups[1].Value).Where(n => char.IsLetter(n[0])).Distinct(StringComparer.Ordinal).ToList();
        foreach (var name in externs)
        {
            sb.Append(".assembly extern ").Append(name).AppendLine(" {}");
        }

        // The scaffold takes the fixture's own name, so the oracle maps both modules' own scope onto it.
        sb.Append(".assembly ").Append(self).AppendLine(" {}");
        // A member of a generic type names the type's parameters, so the scaffold declares them too;
        // their constraints play no part in the body's bytes.
        var owner = method.Method.DeclaringType;
        var typeParameters = owner is { IsGenericTypeDefinition: true }
            ? "<" + string.Join(", ", owner.GetGenericArguments().Select(p => TypeNameFormatter.IlAsmIdentifier(p.Name))) + ">"
            : "";
        sb.Append(".class public auto ansi beforefieldinit N.T").Append(typeParameters).AppendLine(" extends [System.Runtime]System.Object");
        sb.AppendLine("{");
        sb.Append("  ").Append(method.Header).AppendLine();
        sb.AppendLine("  {");
        sb.Append("    .maxstack ").Append(method.MaxStack.ToString(CultureInfo.InvariantCulture)).AppendLine();
        sb.Append(locals);
        sb.Append(body);
        sb.AppendLine("  }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static IEnumerable<string> ClauseWords(string line)
    {
        var word = line.TrimStart('}', ' ').Split(' ', 2)[0];
        return word is ".try" or "catch" or "filter" or "finally" or "fault" ? [word] : [];
    }

    private sealed record IldasmMethod(List<(int Offset, string Opcode, string Operand)> Instructions, int MaxStack, bool InitLocals, List<string> ClauseWords);

    private static Dictionary<string, IldasmMethod> ParseIldasm(string text)
    {
        var methods = new Dictionary<string, IldasmMethod>(StringComparer.Ordinal);
        var classes = new Stack<(string Name, int Depth)>();
        var depth = 0;
        string? pendingClass = null;
        StringBuilder? header = null;
        IldasmMethod? current = null;
        var methodDepth = -1;
        var switchOpen = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.StartsWith(".class ", StringComparison.Ordinal))
            {
                // The name is the last word before extends or implements, without its generic
                // parameter list, which may hold spaces, and without quotes.
                var head = line;
                foreach (var stop in new[] { " extends ", " implements ", " {" })
                {
                    var at = head.IndexOf(stop, StringComparison.Ordinal);
                    if (at >= 0)
                    {
                        head = head[..at];
                    }
                }

                // A generic parameter list follows the name, after any quotes the name itself carries.
                var angle = head.IndexOf('<', head.LastIndexOf('\'') + 1);
                if (angle >= 0)
                {
                    head = head[..angle];
                }

                pendingClass = Unquote(head[(head.LastIndexOf(' ') + 1)..]);
                continue;
            }

            if (header is not null)
            {
                if (line.StartsWith('{'))
                {
                    var joined = header.ToString();
                    var key = string.Join("/", classes.Reverse().Select(c => c.Name)) + "::" + MethodName(joined) + "/" + ParameterCount(joined).ToString(CultureInfo.InvariantCulture);
                    current = new IldasmMethod([], 0, false, []);
                    methods[key] = current;
                    header = null;
                    methodDepth = depth;
                    depth++;
                    continue;
                }

                header.Append(' ').Append(line);
                continue;
            }

            if (line.StartsWith(".method ", StringComparison.Ordinal))
            {
                header = new StringBuilder(line);
                continue;
            }

            if (current is not null)
            {
                if (switchOpen)
                {
                    // A switch table continues on the lines after the instruction until its closing parenthesis.
                    var last = current.Instructions[^1];
                    current.Instructions[^1] = (last.Offset, last.Opcode, last.Operand + " " + line);
                    switchOpen = !line.Contains(')', StringComparison.Ordinal);
                    continue;
                }

                var instruction = InstructionLine().Match(line);
                if (instruction.Success)
                {
                    var operand = instruction.Groups[3].Value.Trim();
                    current.Instructions.Add((int.Parse(instruction.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture), instruction.Groups[2].Value, operand));
                    switchOpen = instruction.Groups[2].Value == "switch" && !operand.Contains(')', StringComparison.Ordinal);
                    continue;
                }

                if (line.StartsWith(".maxstack ", StringComparison.Ordinal))
                {
                    current = current with { MaxStack = int.Parse(line[10..].Trim(), CultureInfo.InvariantCulture) };
                    methods[methods.First(p => ReferenceEquals(p.Value.Instructions, current.Instructions)).Key] = current;
                    continue;
                }

                if (line.StartsWith(".locals", StringComparison.Ordinal))
                {
                    current = current with { InitLocals = line.StartsWith(".locals init", StringComparison.Ordinal) };
                    methods[methods.First(p => ReferenceEquals(p.Value.Instructions, current.Instructions)).Key] = current;
                    continue;
                }

                current.ClauseWords.AddRange(ClauseWords(line));
            }

            if (line.StartsWith('{'))
            {
                if (pendingClass is not null)
                {
                    classes.Push((pendingClass, depth));
                    pendingClass = null;
                }

                depth++;
            }
            else if (line.StartsWith('}'))
            {
                depth--;
                if (current is not null && depth == methodDepth)
                {
                    current = null;
                    methodDepth = -1;
                }

                if (classes.Count > 0 && classes.Peek().Depth == depth)
                {
                    classes.Pop();
                }
            }
        }

        return methods;
    }

    /// <summary>
    /// The index of the parenthesis that opens the parameter list: the first one outside angle
    /// brackets and quotes, since a return type may carry generic arguments and a name may be quoted.
    /// </summary>
    private static int ParameterListStart(string header)
    {
        var angle = 0;
        var quoted = false;
        for (var i = 0; i < header.Length; i++)
        {
            var c = header[i];
            if (c == '\'')
            {
                quoted = !quoted;
            }
            else if (quoted)
            {
                continue;
            }
            else if (c == '<')
            {
                angle++;
            }
            else if (c == '>')
            {
                angle--;
            }
            else if (c == '(' && angle == 0)
            {
                return i;
            }
        }

        throw new AssertFailedException("no parameter list in " + header);
    }

    private static string MethodName(string header)
    {
        // The name is the token before the parameter list; a generic method carries its parameters
        // in angle brackets after the name, and a compiler-made name sits in quotes.
        var open = ParameterListStart(header);
        var end = open;
        while (end > 0 && header[end - 1] == ' ')
        {
            end--;
        }

        var i = end;
        if (header[i - 1] == '>')
        {
            var angle = 0;
            while (i > 0)
            {
                i--;
                if (header[i] == '>')
                {
                    angle++;
                }
                else if (header[i] == '<' && --angle == 0)
                {
                    break;
                }
            }

            end = i;
        }

        var quoted = header[end - 1] == '\'';
        var start = quoted ? header.LastIndexOf('\'', end - 2) : header.LastIndexOf(' ', end - 1) + 1;
        return Unquote(header[start..end]);
    }

    private static int ParameterCount(string header)
    {
        var open = ParameterListStart(header);
        var close = header.IndexOf(") cil", open, StringComparison.Ordinal);
        var inner = header[(open + 1)..(close < 0 ? header.LastIndexOf(')') : close)].Trim();
        if (inner.Length == 0)
        {
            return 0;
        }

        var count = 1;
        var nested = 0;
        foreach (var c in inner)
        {
            if (c is '<' or '(' or '[')
            {
                nested++;
            }
            else if (c is '>' or ')' or ']')
            {
                nested--;
            }
            else if (c == ',' && nested == 0)
            {
                count++;
            }
        }

        return count;
    }

    private static string Unquote(string name) => name.Length > 1 && name[0] == '\'' && name[^1] == '\'' ? name[1..^1] : name;

    [GeneratedRegex(@"^IL_([0-9a-fA-F]{4}):\s+(\S+)(.*)$")]
    private static partial Regex InstructionLine();

    [GeneratedRegex(@"IL_[0-9a-fA-F]{4}")]
    private static partial Regex TargetList();

    [GeneratedRegex(@"\[([A-Za-z][A-Za-z0-9_.]*)\]")]
    private static partial Regex AssemblyHint();
}
