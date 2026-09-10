using IlRepl.Protocol;
using IlRepl.Repl;

namespace IlRepl.Tests.Protocol;

/// <summary>
/// Checks caret classification and complete replacement ranges across supported source forms.
/// </summary>
/// <remarks>
/// Tests for <see cref="CaretClassifier"/>. A spelling carries the caret as <c>|</c>; the
/// expected replacement is the text a row would replace, however far past the caret it runs.
/// </remarks>
[TestClass]
public sealed class CaretClassifierTests
{
    private static readonly CaretClassifier Classifier = new(new CilTokenizer(CilVocabularyBuilder.Vocabulary));

    private static string FixtureDirectory => Path.Combine(AppContext.BaseDirectory, "Protocol", "Fixtures", "highlight");

    /// <summary>
    /// The fixture files, one row each.
    /// </summary>
    public static IEnumerable<object[]> Fixtures => Directory.GetFiles(FixtureDirectory, "*.il").Order(StringComparer.Ordinal).Select(f
        => new object[] { Path.GetFileName(f) });

    /// <summary>
    /// The method opcodes give a method site whose owner is the opcode.
    /// </summary>
    /// <param name="opcode">The opcode.</param>
    [TestMethod]
    [DataRow("call")]
    [DataRow("callvirt")]
    [DataRow("ldftn")]
    [DataRow("ldvirtftn")]
    [DataRow("jmp")]
    public void Classify_CallFamily_IsMethodSite(string opcode)
    {
        var site = Classify($"{opcode} void Console::Wr|iteLine(string)", out var line);
        Assert.AreEqual(CompletionSiteKind.Method, site.Kind);
        Assert.AreEqual(opcode, site.Owner);
        Assert.AreEqual("Wr", site.Prefix);
        Assert.AreEqual("void Console::WriteLine(string)", Replaced(line, site));
        Assert.AreEqual("Console", site.DeclaringTypeText);
        Assert.AreEqual("void", site.ReturnTypeText);
        Assert.IsTrue(site.NextIsParen);
        Assert.IsTrue(site.IsOperand);

        var constructor = Classify("newobj instance void Exception::.c|tor(string)", out line);
        Assert.AreEqual(CompletionSiteKind.Constructor, constructor.Kind);
        Assert.AreEqual(".c", constructor.Prefix);
        Assert.AreEqual("instance void Exception::.ctor(string)", Replaced(line, constructor));
        Assert.IsTrue(constructor.ExplicitInstance);
    }

    /// <summary>
    /// The field opcodes give a field site whose range is the whole reference, field type included.
    /// </summary>
    /// <param name="opcode">The opcode.</param>
    [TestMethod]
    [DataRow("ldsfld")]
    [DataRow("ldsflda")]
    [DataRow("stsfld")]
    [DataRow("ldfld")]
    [DataRow("ldflda")]
    [DataRow("stfld")]
    public void Classify_FieldOpcodes_AreFieldSites(string opcode)
    {
        var site = Classify($"{opcode} string String::Em|pty", out var line);
        Assert.AreEqual(CompletionSiteKind.Field, site.Kind);
        Assert.AreEqual(opcode, site.Owner);
        Assert.AreEqual("Em", site.Prefix);
        Assert.AreEqual("string String::Empty", Replaced(line, site));
        Assert.AreEqual("String", site.DeclaringTypeText);
        Assert.AreEqual("string", site.ReturnTypeText);

        var empty = Classify($"{opcode} int32 Point::|", out line);
        Assert.AreEqual(CompletionSiteKind.Field, empty.Kind);
        Assert.AreEqual("", empty.Prefix);
        Assert.AreEqual("int32 Point::", Replaced(line, empty));
    }

    /// <summary>
    /// Every opcode that takes a type gives a type site on the name.
    /// </summary>
    /// <param name="opcode">The opcode.</param>
    [TestMethod]
    [DataRow("box")]
    [DataRow("castclass")]
    [DataRow("isinst")]
    [DataRow("unbox")]
    [DataRow("unbox.any")]
    [DataRow("newarr")]
    [DataRow("ldelem")]
    [DataRow("ldelema")]
    [DataRow("stelem")]
    [DataRow("ldobj")]
    [DataRow("stobj")]
    [DataRow("cpobj")]
    [DataRow("initobj")]
    [DataRow("sizeof")]
    [DataRow("mkrefany")]
    [DataRow("refanyval")]
    [DataRow("constrained.")]
    [DataRow("ldtoken")]
    public void Classify_TypeOpcodes_AreTypeSites(string opcode)
    {
        var site = Classify($"{opcode} Str|ing", out var line);
        Assert.AreEqual(CompletionSiteKind.Type, site.Kind);
        Assert.AreEqual(opcode, site.Owner);
        Assert.AreEqual("Str", site.Prefix);
        Assert.AreEqual("String", Replaced(line, site));

        var empty = Classify($"{opcode} |", out _);
        Assert.AreEqual(CompletionSiteKind.Type, empty.Kind);
        Assert.AreEqual("", empty.Prefix);
        Assert.AreEqual(0, empty.ReplaceLength);
    }

    /// <summary>
    /// A prefix opcode on the same line as the instruction it prefixes classifies by the second word.
    /// </summary>
    [TestMethod]
    public void Classify_SameLinePrefixThenOpcode_ClassifiesTheSecondWord()
    {
        var site = Classify("tail. call void Console::Wr|iteLine(string)", out var line);
        Assert.AreEqual(CompletionSiteKind.Method, site.Kind);
        Assert.AreEqual("call", site.Owner);
        Assert.AreEqual("void Console::WriteLine(string)", Replaced(line, site));

        var constrained = Classify("constrained. String callvirt instance string Object::To|String()", out line);
        Assert.AreEqual(CompletionSiteKind.Method, constrained.Kind);
        Assert.AreEqual("callvirt", constrained.Owner);
        Assert.AreEqual("instance string Object::ToString()", Replaced(line, constrained));

        var unaligned = Classify("unaligned. 4 ldsfld int32 Point::x|", out _);
        Assert.AreEqual(CompletionSiteKind.Field, unaligned.Kind);
        Assert.AreEqual("ldsfld", unaligned.Owner);

        var type = Classify("constrained. Str|ing", out _);
        Assert.AreEqual(CompletionSiteKind.Type, type.Kind);
        Assert.AreEqual("constrained.", type.Owner);

        Assert.AreEqual(CompletionSiteKind.None, Classify("tail. |call void Console::WriteLine(string)", out _).Kind);
        Assert.AreEqual(CompletionSiteKind.None, Classify("unaligned. 4 ldind.i4|", out _).Kind);
    }

    /// <summary>
    /// <c>ldtoken</c> takes a type, or a member after the <c>method</c> or <c>field</c> keyword.
    /// </summary>
    [TestMethod]
    public void Classify_Ldtoken_TypeThenMethodOrFieldKeyword()
    {
        var type = Classify("ldtoken Str|ing", out _);
        Assert.AreEqual(CompletionSiteKind.Type, type.Kind);
        Assert.AreEqual("ldtoken", type.Owner);

        var method = Classify("ldtoken method void Console::Wr|iteLine(string)", out var line);
        Assert.AreEqual(CompletionSiteKind.Method, method.Kind);
        Assert.AreEqual("ldtoken method", method.Owner);
        Assert.AreEqual("void Console::WriteLine(string)", Replaced(line, method));

        var field = Classify("ldtoken field int32 Point::x|", out line);
        Assert.AreEqual(CompletionSiteKind.Field, field.Kind);
        Assert.AreEqual("ldtoken field", field.Owner);
        Assert.AreEqual("int32 Point::x", Replaced(line, field));

        Assert.AreEqual(CompletionSiteKind.None, Classify("ldtoken metho|d", out _).Kind);
        Assert.AreEqual(CompletionSiteKind.None, Classify("ldtoken method|", out _).Kind);
    }

    /// <summary>
    /// Branches, <c>leave</c>, and <c>switch</c> take labels; a switch site counts its position.
    /// </summary>
    [TestMethod]
    public void Classify_BranchLeaveSwitch_AreLabelSites()
    {
        var br = Classify("br L|1", out var line);
        Assert.AreEqual(CompletionSiteKind.Label, br.Kind);
        Assert.AreEqual("br", br.Owner);
        Assert.AreEqual("L", br.Prefix);
        Assert.AreEqual("L1", Replaced(line, br));

        var leave = Classify("leave.s DO|NE", out line);
        Assert.AreEqual(CompletionSiteKind.Label, leave.Kind);
        Assert.AreEqual("DONE", Replaced(line, leave));

        var brtrue = Classify("brtrue |", out _);
        Assert.AreEqual(CompletionSiteKind.Label, brtrue.Kind);
        Assert.AreEqual("", brtrue.Prefix);

        var second = Classify("switch (A, B|, C)", out line);
        Assert.AreEqual(CompletionSiteKind.Label, second.Kind);
        Assert.AreEqual("switch", second.Owner);
        Assert.AreEqual(1, second.ArgumentIndex);
        Assert.AreEqual("B", Replaced(line, second));

        var third = Classify("switch (A, B, |", out _);
        Assert.AreEqual(CompletionSiteKind.Label, third.Kind);
        Assert.AreEqual(2, third.ArgumentIndex);
        Assert.AreEqual(0, third.ReplaceLength);

        Assert.AreEqual(CompletionSiteKind.None, Classify("switch |(A)", out _).Kind);
    }

    /// <summary>
    /// The local opcodes take locals and the argument opcodes take arguments, by name or index.
    /// </summary>
    [TestMethod]
    public void Classify_LocalAndArgumentOpcodes_AreVariableSites()
    {
        foreach (var opcode in new[] { "ldloc", "ldloc.s", "stloc", "stloc.s", "ldloca", "ldloca.s" })
        {
            var site = Classify($"{opcode} x|", out var line);
            Assert.AreEqual(CompletionSiteKind.Local, site.Kind, opcode);
            Assert.AreEqual(opcode, site.Owner);
            Assert.AreEqual("x", Replaced(line, site));
        }

        foreach (var opcode in new[] { "ldarg", "ldarg.s", "starg", "starg.s", "ldarga", "ldarga.s" })
        {
            var site = Classify($"{opcode} val|ue", out var line);
            Assert.AreEqual(CompletionSiteKind.Argument, site.Kind, opcode);
            Assert.AreEqual(opcode, site.Owner);
            Assert.AreEqual("val", site.Prefix);
            Assert.AreEqual("value", Replaced(line, site));
        }

        var index = Classify("ldloc 0|", out var indexed);
        Assert.AreEqual(CompletionSiteKind.Local, index.Kind);
        Assert.AreEqual("0", Replaced(indexed, index));

        var empty = Classify("ldarg |", out _);
        Assert.AreEqual(CompletionSiteKind.Argument, empty.Kind);
        Assert.AreEqual("", empty.Prefix);
        Assert.AreEqual(CompletionSiteKind.None, Classify("ldloc.0|", out _).Kind);
    }

    /// <summary>
    /// A bare name after <c>call</c> is a member head: a session method or a declaring type to come.
    /// </summary>
    [TestMethod]
    public void Classify_BareCall_IsMemberHead()
    {
        var site = Classify("call Fi|", out var line);
        Assert.AreEqual(CompletionSiteKind.MemberHead, site.Kind);
        Assert.AreEqual("call", site.Owner);
        Assert.AreEqual("Fi", site.Prefix);
        Assert.AreEqual("Fi", Replaced(line, site));
        Assert.IsFalse(site.NextIsDoubleColon);
        Assert.IsFalse(site.NextIsParen);

        var withParens = Classify("call Fib|(int32)", out line);
        Assert.AreEqual(CompletionSiteKind.MemberHead, withParens.Kind);
        Assert.AreEqual("Fib", Replaced(line, withParens));
        Assert.IsTrue(withParens.NextIsParen);

        var typed = Classify("call int32 Fi|", out line);
        Assert.AreEqual(CompletionSiteKind.MemberHead, typed.Kind);
        Assert.AreEqual("int32", typed.ReturnTypeText);
        Assert.AreEqual("Fi", Replaced(line, typed));

        var instance = Classify("call instance |", out _);
        Assert.AreEqual(CompletionSiteKind.MemberHead, instance.Kind);
        Assert.IsTrue(instance.ExplicitInstance);
        Assert.AreEqual("", instance.Prefix);

        var empty = Classify("call |", out _);
        Assert.AreEqual(CompletionSiteKind.MemberHead, empty.Kind);
        Assert.AreEqual(0, empty.ReplaceLength);

        var parameter = Classify("call Fib(int|32)", out line);
        Assert.AreEqual(CompletionSiteKind.Type, parameter.Kind);
        Assert.AreEqual(0, parameter.ArgumentIndex);
        Assert.AreEqual("int32", Replaced(line, parameter));
    }

    /// <summary>
    /// Replaces the entire member reference, including text beyond the caret, after <c>::</c>.
    /// </summary>
    /// <remarks>
    /// After <c>::</c> the range is the whole reference, so accepting a row replaces the reference
    /// once and leaves nothing of the old name behind the caret.
    /// </remarks>
    [TestMethod]
    public void Classify_AfterDoubleColon_RangeIsTheWholeReference()
    {
        var site = Classify("call Console::Wr|iteLine(string)", out var line);
        Assert.AreEqual(CompletionSiteKind.Method, site.Kind);
        Assert.AreEqual("Wr", site.Prefix);
        Assert.AreEqual("Console::WriteLine(string)", Replaced(line, site));
        Assert.AreEqual(5, site.ReplaceStart);
        Assert.AreEqual("Console", site.DeclaringTypeText);
        Assert.IsNull(site.ReturnTypeText);
        Assert.IsTrue(site.NextIsParen);
        Assert.IsFalse(site.NextIsAngle);

        var hinted = Classify("call void [System.Console]System.Console::Wri|teLine(string)", out line);
        Assert.AreEqual("void [System.Console]System.Console::WriteLine(string)", Replaced(line, hinted));
        Assert.AreEqual("[System.Console]System.Console", hinted.DeclaringTypeText);

        var typed = Classify("call Math::Ma|", out line);
        Assert.AreEqual(CompletionSiteKind.Method, typed.Kind);
        Assert.AreEqual("Ma", typed.Prefix);
        Assert.AreEqual("Math::Ma", Replaced(line, typed));
        Assert.IsFalse(typed.NextIsParen);

        var empty = Classify("call Math::|", out line);
        Assert.AreEqual(CompletionSiteKind.Method, empty.Kind);
        Assert.AreEqual("", empty.Prefix);
        Assert.AreEqual("Math::", Replaced(line, empty));
        Assert.AreEqual("Math", empty.DeclaringTypeText);

        var generic = Classify("call Dictionary<string, List<int32>>::A|dd(!0, !1)", out line);
        Assert.AreEqual("Dictionary<string, List<int32>>::Add(!0, !1)", Replaced(line, generic));
        Assert.AreEqual("Dictionary<string, List<int32>>", generic.DeclaringTypeText);

        var constructor = Classify("newobj Exception::|", out line);
        Assert.AreEqual(CompletionSiteKind.Constructor, constructor.Kind);
        Assert.AreEqual("Exception::", Replaced(line, constructor));
    }

    /// <summary>
    /// Replaces only the declaring type when an existing <c>::</c> follows it.
    /// </summary>
    /// <remarks>
    /// In the declaring type the range is the type alone and the <c>::</c> that follows is reported,
    /// so accepting a type inserts no second one.
    /// </remarks>
    [TestMethod]
    public void Classify_InDeclaringType_RangeIsTheTypeAndNextIsDoubleColon()
    {
        var site = Classify("call console|::WriteLine(string)", out var line);
        Assert.AreEqual(CompletionSiteKind.MemberHead, site.Kind);
        Assert.AreEqual("console", site.Prefix);
        Assert.AreEqual("console", Replaced(line, site));
        Assert.IsTrue(site.NextIsDoubleColon);

        var typed = Classify("call instance int32 Poi|nt::Sum()", out line);
        Assert.AreEqual(CompletionSiteKind.MemberHead, typed.Kind);
        Assert.AreEqual("Point", Replaced(line, typed));
        Assert.AreEqual("int32", typed.ReturnTypeText);
        Assert.IsTrue(typed.ExplicitInstance);
        Assert.IsTrue(typed.NextIsDoubleColon);

        var hinted = Classify("call class [System.Runtime]System.Str|ing::Concat(string, string)", out line);
        Assert.AreEqual(CompletionSiteKind.MemberHead, hinted.Kind);
        Assert.AreEqual("System.Str", hinted.Prefix);
        Assert.AreEqual("[System.Runtime]System.String", Replaced(line, hinted));

        var returnType = Classify("call instance i|nt32 Point::Sum()", out line);
        Assert.AreEqual(CompletionSiteKind.Type, returnType.Kind);
        Assert.AreEqual("int32", Replaced(line, returnType));
        Assert.IsTrue(returnType.ExplicitInstance);

        var open = Classify("call Dictionary<string, int32>|", out line);
        Assert.AreEqual(CompletionSiteKind.MemberHead, open.Kind);
        Assert.AreEqual("Dictionary<string, int32>", Replaced(line, open));
        Assert.IsFalse(open.NextIsDoubleColon);
    }

    /// <summary>
    /// Inside a parameter list each parameter is a type site with its index.
    /// </summary>
    [TestMethod]
    public void Classify_InsideParameterList_IsTypeSiteForThatParameter()
    {
        var site = Classify("call instance int32 Point::Sum(int32, str|ing)", out var line);
        Assert.AreEqual(CompletionSiteKind.Type, site.Kind);
        Assert.AreEqual("call", site.Owner);
        Assert.AreEqual("str", site.Prefix);
        Assert.AreEqual("string", Replaced(line, site));
        Assert.AreEqual(1, site.ArgumentIndex);
        Assert.AreEqual("Point", site.DeclaringTypeText);

        var first = Classify("call void Console::WriteLine(|", out _);
        Assert.AreEqual(CompletionSiteKind.Type, first.Kind);
        Assert.AreEqual(0, first.ArgumentIndex);
        Assert.AreEqual(0, first.ReplaceLength);

        var signature = Classify("calli int32(int32, str|ing)", out line);
        Assert.AreEqual(CompletionSiteKind.Type, signature.Kind);
        Assert.AreEqual("calli", signature.Owner);
        Assert.AreEqual(1, signature.ArgumentIndex);
        Assert.AreEqual("string", Replaced(line, signature));

        var returnType = Classify("calli unmanaged cdecl int3|2(int32)", out line);
        Assert.AreEqual(CompletionSiteKind.Type, returnType.Kind);
        Assert.AreEqual("int32", Replaced(line, returnType));
        Assert.AreEqual(-1, returnType.ArgumentIndex);
    }

    /// <summary>
    /// Classifies the innermost generic argument with its owner and preceding argument count.
    /// </summary>
    /// <remarks>
    /// Inside <c>&lt;...&gt;</c> the site is a type argument that knows its owner, its position, and
    /// how many arguments come before it; the innermost list wins.
    /// </remarks>
    [TestMethod]
    public void Classify_InsideGenericArguments_IsTypeArgument_CountingSupplied()
    {
        var first = Classify("call Enumerable::Empty<str|ing>", out var line);
        Assert.AreEqual(CompletionSiteKind.TypeArgument, first.Kind);
        Assert.AreEqual("Enumerable::Empty", first.GenericOwnerText);
        Assert.AreEqual(0, first.ArgumentIndex);
        Assert.AreEqual(0, first.SuppliedArguments);
        Assert.AreEqual("string", Replaced(line, first));

        var open = Classify("call Enumerable::Empty<|", out _);
        Assert.AreEqual(CompletionSiteKind.TypeArgument, open.Kind);
        Assert.AreEqual("", open.Prefix);
        Assert.AreEqual("Enumerable::Empty", open.GenericOwnerText);

        var second = Classify("call Dictionary<string, |>::Add(!0, !1)", out _);
        Assert.AreEqual(CompletionSiteKind.TypeArgument, second.Kind);
        Assert.AreEqual("Dictionary", second.GenericOwnerText);
        Assert.AreEqual(1, second.ArgumentIndex);
        Assert.AreEqual(1, second.SuppliedArguments);

        var nested = Classify("call Dictionary<string, List<in|t32>>::Add(!0, !1)", out line);
        Assert.AreEqual(CompletionSiteKind.TypeArgument, nested.Kind);
        Assert.AreEqual("List", nested.GenericOwnerText);
        Assert.AreEqual(0, nested.ArgumentIndex);
        Assert.AreEqual("int32", Replaced(line, nested));

        var afterInner = Classify("call Dictionary<string, List<int32>|", out line);
        Assert.AreEqual(CompletionSiteKind.TypeArgument, afterInner.Kind);
        Assert.AreEqual("Dictionary", afterInner.GenericOwnerText);
        Assert.AreEqual(1, afterInner.ArgumentIndex);
        Assert.AreEqual("List<int32>", Replaced(line, afterInner));

        var local = Classify(".locals init (int32 a, List<|", out _);
        Assert.AreEqual(CompletionSiteKind.TypeArgument, local.Kind);
        Assert.AreEqual(".locals", local.Owner);
        Assert.AreEqual("List", local.GenericOwnerText);

        var hinted = Classify("box [System.Runtime]System.Collections.Generic.List`1<[System.Runtime]System.Str|ing>", out line);
        Assert.AreEqual(CompletionSiteKind.TypeArgument, hinted.Kind);
        Assert.AreEqual("[System.Runtime]System.Collections.Generic.List`1", hinted.GenericOwnerText);
        Assert.AreEqual("[System.Runtime]System.String", Replaced(line, hinted));
    }

    /// <summary>
    /// Offers signature completion after a generic method's closing angle bracket.
    /// </summary>
    /// <remarks>
    /// Right after a generic method's closed <c>&gt;</c>, with no parameter list yet, the site is
    /// the signature to come.
    /// </remarks>
    [TestMethod]
    public void Classify_AfterClosingAngle_IsSignatureSite()
    {
        var site = Classify("call Enumerable::Empty<string>|", out _);
        Assert.AreEqual(CompletionSiteKind.Signature, site.Kind);
        Assert.AreEqual("Enumerable", site.DeclaringTypeText);
        Assert.AreEqual("Enumerable::Empty", site.GenericOwnerText);
        Assert.AreEqual(0, site.ReplaceLength);

        var parameters = Classify("call Enumerable::Empty<string>(|)", out _);
        Assert.AreEqual(CompletionSiteKind.Type, parameters.Kind);
        Assert.AreEqual(0, parameters.ArgumentIndex);

        Assert.AreEqual(CompletionSiteKind.None, Classify("call Enumerable::Empty<string>(int32)|", out _).Kind);
    }

    /// <summary>
    /// Offers types in declaration lists while excluding variable names, slot numbers, and initializers.
    /// </summary>
    [TestMethod]
    public void Classify_LocalsAndArgs_TypesInsideParens()
    {
        var local = Classify(".locals init (int32 a, Str|ing s)", out var line);
        Assert.AreEqual(CompletionSiteKind.Type, local.Kind);
        Assert.AreEqual(".locals", local.Owner);
        Assert.AreEqual(1, local.ArgumentIndex);
        Assert.AreEqual("String", Replaced(line, local));

        var slot = Classify(".locals init ([0] int|32 a)", out line);
        Assert.AreEqual(CompletionSiteKind.Type, slot.Kind);
        Assert.AreEqual("int32", Replaced(line, slot));

        var next = Classify(".locals init (int32 a, |)", out _);
        Assert.AreEqual(CompletionSiteKind.Type, next.Kind);
        Assert.AreEqual(1, next.ArgumentIndex);
        Assert.AreEqual("", next.Prefix);

        var argument = Classify(".args (int32 n = 0, str|ing s = \"x\")", out line);
        Assert.AreEqual(CompletionSiteKind.Type, argument.Kind);
        Assert.AreEqual(".args", argument.Owner);
        Assert.AreEqual("string", Replaced(line, argument));

        var typeArgument = Classify(".typeargs (Str|ing, int32)", out line);
        Assert.AreEqual(CompletionSiteKind.Type, typeArgument.Kind);
        Assert.AreEqual(".typeargs", typeArgument.Owner);
        Assert.AreEqual("String", Replaced(line, typeArgument));

        Assert.AreEqual(CompletionSiteKind.None, Classify(".locals init (int32 a|)", out _).Kind);
        Assert.AreEqual(CompletionSiteKind.None, Classify(".locals init ([0|] int32 a)", out _).Kind);
        Assert.AreEqual(CompletionSiteKind.None, Classify(".locals init (int32 a, [in|] int32 b)", out _).Kind);
        Assert.AreEqual(CompletionSiteKind.None, Classify(".args (int32 n = 4|2)", out _).Kind);
        Assert.AreEqual(CompletionSiteKind.None, Classify(".locals ini|t (int32 a)", out _).Kind);
    }

    /// <summary>
    /// Offers method-header return, parameter, and constraint types while excluding names and modifiers.
    /// </summary>
    /// <remarks>
    /// A method header offers types for the return type, each parameter, and each constraint;
    /// the modifiers, the name, and the generic parameter names are not sites.
    /// </remarks>
    [TestMethod]
    public void Classify_MethodHeader_ReturnParameterAndConstraintTypes()
    {
        var returnType = Classify(".method public static int|32 Add(int32 a, int32 b)", out var line);
        Assert.AreEqual(CompletionSiteKind.Type, returnType.Kind);
        Assert.AreEqual(".method", returnType.Owner);
        Assert.AreEqual("int32", Replaced(line, returnType));
        Assert.AreEqual(-1, returnType.ArgumentIndex);
        Assert.IsTrue(returnType.DeclarationComplete);

        var parameter = Classify(".method public static int32 Add(int32 a, in|t32 b)", out line);
        Assert.AreEqual(CompletionSiteKind.Type, parameter.Kind);
        Assert.AreEqual(1, parameter.ArgumentIndex);
        Assert.AreEqual("int32", Replaced(line, parameter));

        var constraint = Classify(".method public static void M<(class IComp|arable) T>(!!0 x)", out line);
        Assert.AreEqual(CompletionSiteKind.Type, constraint.Kind);
        Assert.AreEqual(".method", constraint.Owner);
        Assert.AreEqual("IComparable", Replaced(line, constraint));

        var empty = Classify(".method public static |", out _);
        Assert.AreEqual(CompletionSiteKind.Type, empty.Kind);
        Assert.AreEqual("", empty.Prefix);

        Assert.AreEqual(CompletionSiteKind.None, Classify(".method publ|ic static int32 Add()", out _).Kind);
        Assert.AreEqual(CompletionSiteKind.None, Classify(".method public static int32 Ad|d(int32 a)", out _).Kind);
        Assert.AreEqual(CompletionSiteKind.None, Classify(".method public static void M<T|>(!!0 x)", out _).Kind);
        Assert.AreEqual(CompletionSiteKind.None, Classify(".method public static int32 Add(int32 a|)", out _).Kind);
        Assert.AreEqual(CompletionSiteKind.None, Classify(".method public static int32 Add(int32 a) cil managed|", out _).Kind);
        Assert.AreEqual(CompletionSiteKind.None, Classify(".method public static int32 Add(int32 a) {|", out _).Kind);
    }

    /// <summary>
    /// Offers class-header base, interface, and constraint types while excluding the class name.
    /// </summary>
    /// <remarks>
    /// A class header offers types after <c>extends</c>, for each <c>implements</c> item, and for
    /// each constraint; the name is not a site.
    /// </remarks>
    [TestMethod]
    public void Classify_ClassHeader_ExtendsImplementsAndConstraints()
    {
        var extends = Classify(".class public Point extends Sys|tem.ValueType", out var line);
        Assert.AreEqual(CompletionSiteKind.Type, extends.Kind);
        Assert.AreEqual("extends", extends.Owner);
        Assert.AreEqual("Sys", extends.Prefix);
        Assert.AreEqual("System.ValueType", Replaced(line, extends));
        Assert.IsTrue(extends.DeclarationComplete);

        var emptyBase = Classify(".class public Point extends |", out _);
        Assert.AreEqual(CompletionSiteKind.Type, emptyBase.Kind);
        Assert.AreEqual("extends", emptyBase.Owner);

        var implements = Classify(".class public Point extends System.ValueType implements IComparable, IDis|posable", out line);
        Assert.AreEqual(CompletionSiteKind.Type, implements.Kind);
        Assert.AreEqual("implements", implements.Owner);
        Assert.AreEqual(1, implements.ArgumentIndex);
        Assert.AreEqual("IDisposable", Replaced(line, implements));

        var nextInterface = Classify(".class public Point implements IComparable, |", out _);
        Assert.AreEqual(CompletionSiteKind.Type, nextInterface.Kind);
        Assert.AreEqual("implements", nextInterface.Owner);
        Assert.AreEqual(1, nextInterface.ArgumentIndex);

        var constraint = Classify(".class public Box<(class IComp|arable) T>", out line);
        Assert.AreEqual(CompletionSiteKind.Type, constraint.Kind);
        Assert.AreEqual(".class", constraint.Owner);
        Assert.AreEqual("IComparable", Replaced(line, constraint));

        Assert.AreEqual(CompletionSiteKind.None, Classify(".class public Poi|nt", out _).Kind);
        Assert.AreEqual(CompletionSiteKind.None, Classify(".class public |", out _).Kind);
        Assert.AreEqual(CompletionSiteKind.None, Classify(".class public Point exte|nds Object", out _).Kind);
        Assert.AreEqual(CompletionSiteKind.None, Classify(".class public Box<T|>", out _).Kind);
    }

    /// <summary>
    /// A property header offers types for the property type and for each indexer parameter.
    /// </summary>
    [TestMethod]
    public void Classify_PropertyHeader_TypeAndIndexerParameters()
    {
        var type = Classify(".property instance int|32 Count()", out var line);
        Assert.AreEqual(CompletionSiteKind.Type, type.Kind);
        Assert.AreEqual(".property", type.Owner);
        Assert.AreEqual("int32", Replaced(line, type));
        Assert.IsTrue(type.DeclarationComplete);

        var parameter = Classify(".property int32 Item(int32 i, Str|ing s)", out line);
        Assert.AreEqual(CompletionSiteKind.Type, parameter.Kind);
        Assert.AreEqual(1, parameter.ArgumentIndex);
        Assert.AreEqual("String", Replaced(line, parameter));

        var empty = Classify(".property instance int32 Count(|)", out _);
        Assert.AreEqual(CompletionSiteKind.Type, empty.Kind);
        Assert.AreEqual(0, empty.ArgumentIndex);

        Assert.AreEqual(CompletionSiteKind.None, Classify(".property int32 It|em(int32 i)", out _).Kind);
        Assert.AreEqual(CompletionSiteKind.None, Classify(".property int32 Item(int32 i|)", out _).Kind);
    }

    /// <summary>
    /// Offers field and event types and complete accessor method signatures.
    /// </summary>
    /// <remarks>
    /// A field or event header offers its type; an accessor directive offers the open type's
    /// methods, with the whole declared signature as the range.
    /// </remarks>
    [TestMethod]
    public void Classify_EventAndAccessors_AreSites()
    {
        var field = Classify(".field public static initonly Str|ing s", out var line);
        Assert.AreEqual(CompletionSiteKind.Type, field.Kind);
        Assert.AreEqual(".field", field.Owner);
        Assert.AreEqual("String", Replaced(line, field));

        var eventType = Classify(".event EventHan|dler Changed", out line);
        Assert.AreEqual(CompletionSiteKind.Type, eventType.Kind);
        Assert.AreEqual(".event", eventType.Owner);
        Assert.AreEqual("EventHandler", Replaced(line, eventType));

        foreach (var directive in new[] { ".get", ".set", ".other", ".addon", ".removeon", ".fire" })
        {
            var qualified = Classify($"{directive} instance int32 Point::get_Co|unt()", out line);
            Assert.AreEqual(CompletionSiteKind.Method, qualified.Kind, directive);
            Assert.AreEqual(directive, qualified.Owner);
            Assert.AreEqual("instance int32 Point::get_Count()", Replaced(line, qualified));

            var declared = Classify($"{directive} int32 get_Co|unt()", out line);
            Assert.AreEqual(CompletionSiteKind.Method, declared.Kind, directive);
            Assert.AreEqual(directive, declared.Owner);
            Assert.AreEqual("get_Co", declared.Prefix);
            Assert.AreEqual("int32 get_Count()", Replaced(line, declared));
            Assert.AreEqual("int32", declared.ReturnTypeText);
            Assert.IsTrue(declared.NextIsParen);
        }

        var accessorParameter = Classify(".set instance void set_Count(int|32)", out line);
        Assert.AreEqual(CompletionSiteKind.Type, accessorParameter.Kind);
        Assert.AreEqual("int32", Replaced(line, accessorParameter));
        Assert.IsTrue(accessorParameter.ExplicitInstance);

        var empty = Classify(".get |", out _);
        Assert.AreEqual(CompletionSiteKind.MemberHead, empty.Kind);
        Assert.AreEqual(".get", empty.Owner);

        Assert.AreEqual(CompletionSiteKind.None, Classify(".field public int32 x|", out _).Kind);
        Assert.AreEqual(CompletionSiteKind.None, Classify(".field public int32 x = 4|2", out _).Kind);
        Assert.AreEqual(CompletionSiteKind.None, Classify(".event EventHandler Chan|ged", out _).Kind);
    }

    /// <summary>
    /// Classifies both override members and the custom-attribute constructor before its initializer.
    /// </summary>
    /// <remarks>
    /// <c>.override</c> takes a member before and after <c>with</c>, each with its own owner;
    /// <c>.custom</c> takes a constructor and nothing after <c>=</c>.
    /// </remarks>
    [TestMethod]
    public void Classify_OverrideAndCustom_AreMemberSites()
    {
        var inBody = Classify(".override Object::To|String", out var line);
        Assert.AreEqual(CompletionSiteKind.Method, inBody.Kind);
        Assert.AreEqual(".override", inBody.Owner);
        Assert.AreEqual("Object::ToString", Replaced(line, inBody));
        Assert.AreEqual("Object", inBody.DeclaringTypeText);

        var keyword = Classify(".override method instance string Object::To|String() with method instance string Point::Show()", out line);
        Assert.AreEqual(CompletionSiteKind.Method, keyword.Kind);
        Assert.AreEqual(".override", keyword.Owner);
        Assert.AreEqual("instance string Object::ToString()", Replaced(line, keyword));

        var with = Classify(".override method instance string Object::ToString() with method instance string Point::Sh|ow()", out line);
        Assert.AreEqual(CompletionSiteKind.Method, with.Kind);
        Assert.AreEqual(".override with", with.Owner);
        Assert.AreEqual("instance string Point::Show()", Replaced(line, with));
        Assert.AreEqual("Point", with.DeclaringTypeText);

        var withType = Classify(".override method instance string Object::ToString() with method instance string Poi|nt::Show()", out line);
        Assert.AreEqual(CompletionSiteKind.MemberHead, withType.Kind);
        Assert.AreEqual(".override with", withType.Owner);
        Assert.AreEqual("Point", Replaced(line, withType));
        Assert.IsTrue(withType.NextIsDoubleColon);

        var custom = Classify(".custom instance void ObsoleteAttribute::.c|tor(string) = (01 00 00 00)", out line);
        Assert.AreEqual(CompletionSiteKind.Constructor, custom.Kind);
        Assert.AreEqual(".custom", custom.Owner);
        Assert.AreEqual("instance void ObsoleteAttribute::.ctor(string)", Replaced(line, custom));

        var customType = Classify(".custom Obs|", out line);
        Assert.AreEqual(CompletionSiteKind.MemberHead, customType.Kind);
        Assert.AreEqual(".custom", customType.Owner);
        Assert.AreEqual("Obs", Replaced(line, customType));

        Assert.AreEqual(CompletionSiteKind.None, Classify(".custom instance void ObsoleteAttribute::.ctor(string) = (01 0|0 00 00)",
            out _).Kind);
        Assert.AreEqual(CompletionSiteKind.None, Classify(".custom instance void ObsoleteAttribute::.ctor(string) = |", out _).Kind);
        Assert.AreEqual(CompletionSiteKind.None, Classify(".override Object::ToString wi|th method Point::Show", out _).Kind);
    }

    /// <summary>
    /// The type after <c>catch</c> is a type site, with or without the closing brace before it.
    /// </summary>
    [TestMethod]
    public void Classify_CatchClause_IsTypeSite()
    {
        var closing = Classify("} catch Exc|eption {", out var line);
        Assert.AreEqual(CompletionSiteKind.Type, closing.Kind);
        Assert.AreEqual("catch", closing.Owner);
        Assert.AreEqual("Exc", closing.Prefix);
        Assert.AreEqual("Exception", Replaced(line, closing));

        var bare = Classify("catch Exc|eption {", out line);
        Assert.AreEqual(CompletionSiteKind.Type, bare.Kind);
        Assert.AreEqual("Exception", Replaced(line, bare));

        var empty = Classify("} catch |", out _);
        Assert.AreEqual(CompletionSiteKind.Type, empty.Kind);
        Assert.AreEqual("", empty.Prefix);

        Assert.AreEqual(CompletionSiteKind.None, Classify("} catch Exception {|", out _).Kind);
        Assert.AreEqual(CompletionSiteKind.None, Classify("} cat|ch Exception {", out _).Kind);
        Assert.AreEqual(CompletionSiteKind.None, Classify("} finally {|", out _).Kind);
        Assert.AreEqual(CompletionSiteKind.None, Classify(".try {|", out _).Kind);
    }

    /// <summary>
    /// Classifies session and qualified method targets for disassembly commands.
    /// </summary>
    /// <remarks>
    /// <c>.dis</c> and <c>.disassemble</c> take a method: a session method as a member head, or a
    /// type's member after <c>::</c>.
    /// </remarks>
    [TestMethod]
    public void Classify_Dis_IsMethodSite()
    {
        var session = Classify(".dis Fi|b", out var line);
        Assert.AreEqual(CompletionSiteKind.MemberHead, session.Kind);
        Assert.AreEqual(".dis", session.Owner);
        Assert.AreEqual("Fi", session.Prefix);
        Assert.AreEqual("Fib", Replaced(line, session));

        var member = Classify(".dis String::Tr|im", out line);
        Assert.AreEqual(CompletionSiteKind.Method, member.Kind);
        Assert.AreEqual(".dis", member.Owner);
        Assert.AreEqual("String::Trim", Replaced(line, member));
        Assert.AreEqual("String", member.DeclaringTypeText);

        var full = Classify(".disassemble Point::Su|m", out line);
        Assert.AreEqual(CompletionSiteKind.Method, full.Kind);
        Assert.AreEqual(".dis", full.Owner);
        Assert.AreEqual("Point::Sum", Replaced(line, full));

        var empty = Classify(".dis |", out _);
        Assert.AreEqual(CompletionSiteKind.MemberHead, empty.Kind);
        Assert.AreEqual(".dis", empty.Owner);

        Assert.AreEqual(CompletionSiteKind.None, Classify(".load Greeter.d|ll", out _).Kind);
        Assert.AreEqual(CompletionSiteKind.None, Classify(".maxstack 8|", out _).Kind);
        Assert.AreEqual(CompletionSiteKind.None, Classify(".param [1] = 4|2", out _).Kind);
        Assert.AreEqual(CompletionSiteKind.None, Classify(".pack 4|", out _).Kind);
    }

    /// <summary>
    /// A caret in the middle of an identifier replaces the whole identifier.
    /// </summary>
    [TestMethod]
    public void Classify_MidIdentifier_RangeCoversTheWholeIdentifier()
    {
        var site = Classify("box Str|ing", out var line);
        Assert.AreEqual(CompletionSiteKind.Type, site.Kind);
        Assert.AreEqual("Str", site.Prefix);
        Assert.AreEqual("String", Replaced(line, site));
        Assert.AreEqual(4, site.ReplaceStart);
        Assert.AreEqual(7, site.Caret);

        var start = Classify("box |String", out line);
        Assert.AreEqual("", start.Prefix);
        Assert.AreEqual("String", Replaced(line, start));

        var qualified = Classify("box System.Text.String|Builder", out line);
        Assert.AreEqual("System.Text.String", qualified.Prefix);
        Assert.AreEqual("System.Text.StringBuilder", Replaced(line, qualified));

        var member = Classify("call Console::Write|Line(string)", out line);
        Assert.AreEqual("Write", member.Prefix);
        Assert.AreEqual("Console::WriteLine(string)", Replaced(line, member));

        var nested = Classify("box Outer/In|ner", out line);
        Assert.AreEqual("Outer/In", nested.Prefix);
        Assert.AreEqual("Outer/Inner", Replaced(line, nested));
    }

    /// <summary>
    /// A quoted identifier is one lexeme, quotes included, in the prefix and the range.
    /// </summary>
    [TestMethod]
    public void Classify_QuotedIdentifier_SpansTheQuotes()
    {
        var type = Classify("box 'Str|ing'", out var line);
        Assert.AreEqual(CompletionSiteKind.Type, type.Kind);
        Assert.AreEqual("'Str", type.Prefix);
        Assert.AreEqual("'String'", Replaced(line, type));

        var member = Classify("call 'A B'::'C |D'()", out line);
        Assert.AreEqual(CompletionSiteKind.Method, member.Kind);
        Assert.AreEqual("'C ", member.Prefix);
        Assert.AreEqual("'A B'::'C D'()", Replaced(line, member));
        Assert.AreEqual("'A B'", member.DeclaringTypeText);

        var mixed = Classify("box N.'<>|c'", out line);
        Assert.AreEqual(CompletionSiteKind.Type, mixed.Kind);
        Assert.AreEqual("N.'<>", mixed.Prefix);
        Assert.AreEqual("N.'<>c'", Replaced(line, mixed));

        var generated = Classify("newobj instance void class [Fixtures]N.'<>c__DisplayClass1_0`1'<int32>::.c|tor()", out _);
        Assert.AreEqual(CompletionSiteKind.Constructor, generated.Kind);
        Assert.AreEqual("[Fixtures]N.'<>c__DisplayClass1_0`1'<int32>", generated.DeclaringTypeText);

        var opcodeName = Classify("call void Hello::'ad|d'(int32, int32)", out line);
        Assert.AreEqual(CompletionSiteKind.Method, opcodeName.Kind);
        Assert.AreEqual("'ad", opcodeName.Prefix);
        Assert.AreEqual("void Hello::'add'(int32, int32)", Replaced(line, opcodeName));
    }

    /// <summary>
    /// Preserves trailing comments, neighboring arguments, and type suffixes outside replacement ranges.
    /// </summary>
    [TestMethod]
    public void Classify_TrailingCommentAndOtherArguments_StayOutsideTheRange()
    {
        var comment = Classify("call Console::Wr|iteLine(string) // prints", out var line);
        Assert.AreEqual("Console::WriteLine(string)", Replaced(line, comment));

        var argument = Classify("switch (A, B|, C)", out line);
        Assert.AreEqual("B", Replaced(line, argument));

        var parameter = Classify("call void M(int32, Str|ing, bool)", out line);
        Assert.AreEqual("String", Replaced(line, parameter));

        var array = Classify("box Str|ing[]", out line);
        Assert.AreEqual("String", Replaced(line, array));

        var pointer = Classify("ldobj Poi|nt*", out line);
        Assert.AreEqual("Point", Replaced(line, pointer));

        var reference = Classify(".locals init (Str|ing& s)", out line);
        Assert.AreEqual("String", Replaced(line, reference));

        var generic = Classify("box List<int|32>[]", out line);
        Assert.AreEqual("int32", Replaced(line, generic));

        var closedGeneric = Classify("box Li|st<int32>", out line);
        Assert.AreEqual("List<int32>", Replaced(line, closedGeneric));
        Assert.AreEqual("Li", closedGeneric.Prefix);

        var openGeneric = Classify("box Li|st<", out line);
        Assert.AreEqual("List", Replaced(line, openGeneric));
        Assert.IsTrue(openGeneric.NextIsAngle);

        Assert.AreEqual(CompletionSiteKind.None, Classify("box String[|]", out _).Kind);
        Assert.AreEqual(CompletionSiteKind.None, Classify("box String[]|", out _).Kind);
    }

    /// <summary>
    /// Distinguishes incomplete components from declarations ready for full confirmation.
    /// </summary>
    /// <remarks>
    /// A declaration missing what its parser needs reports itself incomplete, so a completed
    /// component is confirmed alone; a complete one reports complete.
    /// </remarks>
    [TestMethod]
    public void Classify_UnfinishedDeclarations_ReportIncomplete()
    {
        Assert.IsFalse(Classify(".field int|", out _).DeclarationComplete);
        Assert.IsFalse(Classify(".field public |", out _).DeclarationComplete);
        Assert.IsFalse(Classify(".method vo|", out _).DeclarationComplete);
        Assert.IsFalse(Classify(".method public static |", out _).DeclarationComplete);
        Assert.IsFalse(Classify(".method public static int32 Add(|", out _).DeclarationComplete);
        Assert.IsFalse(Classify(".method public static int32 Add(int32 a, in|", out _).DeclarationComplete);
        Assert.IsFalse(Classify(".locals init (int32 a, Str|", out _).DeclarationComplete);
        Assert.IsFalse(Classify(".locals |", out _).DeclarationComplete);
        Assert.IsFalse(Classify(".args (|", out _).DeclarationComplete);
        Assert.IsFalse(Classify(".property int32 Item(int32 i, Str|", out _).DeclarationComplete);
        Assert.IsFalse(Classify(".event EventHan|dler", out _).DeclarationComplete);
        Assert.IsFalse(Classify(".class public Box<(class IComp|arable) T", out _).DeclarationComplete);

        Assert.IsTrue(Classify(".field int|32 x", out _).DeclarationComplete);
        Assert.IsTrue(Classify(".method public static int|32 Add(int32 a)", out _).DeclarationComplete);
        Assert.IsTrue(Classify(".method public static int32 Add(int32 a, in|t32 b)", out _).DeclarationComplete);
        Assert.IsTrue(Classify(".locals init (int32 a, Str|ing s)", out _).DeclarationComplete);
        Assert.IsTrue(Classify(".locals init (int32 a, Str|)", out _).DeclarationComplete);
        Assert.IsTrue(Classify(".args (int32 n = 0, str|ing s)", out _).DeclarationComplete);
        Assert.IsTrue(Classify(".property int32 Item(int32 i, Str|ing s)", out _).DeclarationComplete);
        Assert.IsTrue(Classify(".event EventHan|dler Changed", out _).DeclarationComplete);
        Assert.IsTrue(Classify(".class public Point extends Sys|tem.ValueType", out _).DeclarationComplete);
        Assert.IsTrue(Classify("call Console::Wr|iteLine(string)", out _).DeclarationComplete);
        Assert.IsTrue(Classify("box Str|ing", out _).DeclarationComplete);
    }

    /// <summary>
    /// Nested argument sites retain the enclosing type and declaration parameter independently of their generic position.
    /// </summary>
    /// <param name="text">The declaration and marked caret.</param>
    /// <param name="typePrefix">The start of the enclosing type.</param>
    /// <param name="parameter">The enclosing declaration parameter index.</param>
    [TestMethod]
    [DataRow(".event System.Action<List<in|>>[] Changed {", "System.Action", -1)]
    [DataRow(".method List<in|> M()", "List", -1)]
    [DataRow(".method void M(string first, List<in|> value)", "List", 1)]
    [DataRow(".field method List<in|> *(int32)", "method", -1)]
    [DataRow(".locals init (int32 first, class List<in|>[] values)", "class List", 1)]
    public void Classify_GenericArgument_RetainsDeclarationType(string text, string typePrefix, int parameter)
    {
        var site = Classify(text, out var line);
        Assert.AreEqual(CompletionSiteKind.TypeArgument, site.Kind);
        Assert.AreEqual(line.IndexOf(typePrefix, StringComparison.Ordinal), site.EnclosingTypeStart);
        Assert.AreEqual(parameter, site.EnclosingParameterIndex);
        Assert.AreEqual(0, site.ArgumentIndex);
    }

    /// <summary>
    /// Numbers, strings, and floats are not sites, and neither is an opcode with no operand.
    /// </summary>
    [TestMethod]
    public void Classify_IntegerStringAndFloatOperands_AreNoSites()
    {
        foreach (var spelling in new[] { "ldc.i4 4|2", "ldc.i4.s |", "ldc.i8 1|", "ldc.r8 1.|5", "ldc.r4 |", "ldstr \"ab|c\"",
            "ldstr \"abc\"|", "ldstr |", "nop|", "nop |", "add |", "ret|", "ldloc.0 |", "ldarg.1|", "unaligned. |", "unaligned. 4|" })
        {
            var site = Classify(spelling, out _);
            Assert.AreEqual(CompletionSiteKind.None, site.Kind, spelling);
            Assert.IsFalse(site.IsOperand, spelling);
        }
    }

    /// <summary>
    /// The first word belongs to the local catalog, whatever it is and wherever the caret is in it.
    /// </summary>
    [TestMethod]
    public void Classify_FirstWord_IsNoSite()
    {
        foreach (var spelling in new[] { "|", "ca|ll", "call|", "|call Console::WriteLine()", "cal|l Console::WriteLine()",
            ".loc|als init (int32 a)", ".meth|od", "bo|x", "L1: ca|ll", "L1: |", "   |", "  bo|x String", "catch|", "cat|ch Exception",
            "}|", "{|", "|}" })
        {
            Assert.AreEqual(CompletionSiteKind.None, Classify(spelling, out _).Kind, spelling);
        }
    }

    /// <summary>
    /// Excludes strings and comments while preserving comment state inherited from preceding lines.
    /// </summary>
    /// <remarks>
    /// A caret inside a comment or a string is never a site, and a block comment open from an
    /// earlier line makes the whole line a comment until it closes.
    /// </remarks>
    [TestMethod]
    public void Classify_CommentsAndStrings_AreSkipped()
    {
        Assert.AreEqual(CompletionSiteKind.None, Classify("call Console::WriteLine(string) // com|ment", out _).Kind);
        Assert.AreEqual(CompletionSiteKind.None, Classify("call Console::WriteLine(string) // comment|", out _).Kind);
        Assert.AreEqual(CompletionSiteKind.None, Classify("call Console::WriteLine() /* Wr|ite */", out _).Kind);
        Assert.AreEqual(CompletionSiteKind.None, Classify("/* Str|ing */ box String", out _).Kind);
        Assert.AreEqual(CompletionSiteKind.None, Classify("ldstr \"Str|ing\"", out _).Kind);
        Assert.AreEqual(CompletionSiteKind.None, Classify("call Console::WriteLine(\"Str|ing\")", out _).Kind);

        Assert.AreEqual(CompletionSiteKind.Type, Classify("/* a */ box Str|ing", out _).Kind);
        Assert.AreEqual(CompletionSiteKind.Type, Classify("box Str|ing /* a", out _).Kind);
        Assert.AreEqual(CompletionSiteKind.Method, Classify("call Console::Wr|iteLine() // c", out _).Kind);

        var open = Classify("box Str|ing", out _, inBlockComment: true);
        Assert.AreEqual(CompletionSiteKind.None, open.Kind);
        var closed = Classify("*/ box Str|ing", out _, inBlockComment: true);
        Assert.AreEqual(CompletionSiteKind.Type, closed.Kind);
        Assert.AreEqual(CompletionSiteKind.None, Classify("a */ box Str|ing", out _, inBlockComment: false).Kind);
    }

    /// <summary>
    /// A leading label, or several, is skipped and the instruction after it classifies as usual.
    /// </summary>
    [TestMethod]
    public void Classify_LeadingLabel_IsSkipped()
    {
        var one = Classify("L1: call Console::Wr|iteLine()", out var line);
        Assert.AreEqual(CompletionSiteKind.Method, one.Kind);
        Assert.AreEqual("Console::WriteLine()", Replaced(line, one));
        Assert.AreEqual(9, one.ReplaceStart);

        var two = Classify("L1: L2: box Str|ing", out line);
        Assert.AreEqual(CompletionSiteKind.Type, two.Kind);
        Assert.AreEqual("String", Replaced(line, two));

        var branch = Classify("LOOP: br LO|OP", out line);
        Assert.AreEqual(CompletionSiteKind.Label, branch.Kind);
        Assert.AreEqual("LOOP", Replaced(line, branch));

        Assert.AreEqual(CompletionSiteKind.None, Classify("L|1: box String", out _).Kind);
        Assert.AreEqual(CompletionSiteKind.None, Classify("L1:| box String", out _).Kind);
    }

    /// <summary>
    /// Checks every fixture caret produces a valid bounded site without throwing.
    /// </summary>
    /// <remarks>
    /// Every caret position of every highlight fixture line classifies without throwing and
    /// answers a site inside the line.
    /// </remarks>
    /// <param name="file">The fixture file.</param>
    [TestMethod]
    [DynamicData(nameof(Fixtures))]
    public void Classify_HighlightFixtures_NeverThrow(string file)
    {
        var tokenizer = new CilTokenizer(CilVocabularyBuilder.Vocabulary);
        var lines = File.ReadAllLines(Path.Combine(FixtureDirectory, file));
        Assert.IsNotEmpty(lines, file);
        var inComment = false;
        var sites = 0;
        foreach (var line in lines)
        {
            var before = inComment;
            tokenizer.Tokenize(line, ref inComment);
            for (var caret = 0; caret <= line.Length; caret++)
            {
                var where = $"{file}: '{line}' at {caret}";
                var site = Classifier.Classify(line, caret, before);
                Assert.IsTrue(site.ReplaceStart >= 0 && site.ReplaceEnd <= line.Length,
                    $"{where}: range {site.ReplaceStart}+{site.ReplaceLength}");
                Assert.AreEqual(caret, site.Caret, where);
                if (site.IsOperand)
                {
                    sites++;
                    Assert.IsLessThanOrEqualTo(caret, site.ReplaceStart, $"{where}: the range starts after the caret");
                    Assert.IsLessThanOrEqualTo(caret - site.ReplaceStart, site.Prefix.Length,
                        $"{where}: prefix '{site.Prefix}' is longer than the range before the caret");
                    Assert.IsTrue(line.AsSpan(0, caret).EndsWith(site.Prefix),
                        $"{where}: prefix '{site.Prefix}' is not the text before the caret");
                }
                else
                {
                    Assert.AreEqual(CompletionSite.None, site with { Caret = 0 }, where);
                }
            }
        }

        Assert.IsTrue(sites > 0 || file is "comments.il" or "literals.il" or "malformed.il" or "commands.il" or "labels.il",
            $"{file}: no site in any line");
    }

    /// <summary>
    /// The site is the same object for the same input, and the caret is clamped to the line.
    /// </summary>
    [TestMethod]
    public void Classify_ClampsTheCaretAndRefusesNull()
    {
        var beyond = Classifier.Classify("box String", 99, false);
        Assert.AreEqual(CompletionSiteKind.Type, beyond.Kind);
        Assert.AreEqual(10, beyond.Caret);
        var before = Classifier.Classify("box String", -5, false);
        Assert.AreEqual(CompletionSiteKind.None, before.Kind);
        Assert.Throws<ArgumentNullException>(() => Classifier.Classify(null!, 0, false));
        Assert.Throws<ArgumentNullException>(() => new CaretClassifier(null!));
    }

    private static CompletionSite Classify(string spelling, out string line, bool inBlockComment = false)
    {
        var caret = spelling.IndexOf('|', StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, caret, $"'{spelling}' has no caret");
        line = spelling.Remove(caret, 1);
        var site = Classifier.Classify(line, caret, inBlockComment);
        Assert.AreEqual(caret, site.Caret, spelling);
        return site;
    }

    private static string Replaced(string line, CompletionSite site) => line.Substring(site.ReplaceStart, site.ReplaceLength);
}
