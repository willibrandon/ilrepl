using System.Reflection;
using IlRepl.Engine;
using IlRepl.Engine.Binding;
using IlRepl.Protocol;

namespace IlRepl.Tests.Engine.Binding;

/// <summary>
/// Completion spellings preserve exact type and member identities when independently resolved by real input.
/// </summary>
[TestClass]
public sealed class CompletionSpellingTests
{
    /// <summary>
    /// Primitive, open, nested, constructed and element types round-trip through the runtime parser.
    /// </summary>
    [TestMethod]
    public void TypeSpellings_SelectTheirRuntimeTypes()
    {
        var session = new Session();
        using var snapshot = BindingSnapshot.Capture(session);
        var speller = new TypeSpeller(new SnapshotBindingScope(snapshot));
        Type[] types = [typeof(int), typeof(void), typeof(string), typeof(Console), typeof(List<>), typeof(List<string>),
            typeof(Dictionary<string, List<int[]>>), typeof(Dictionary<string, int>.KeyCollection),
            typeof(int[]), typeof(int).MakeArrayType(1), typeof(int[,]), typeof(int).MakePointerType(),
            typeof(string).MakeByRefType(), typeof(Environment.SpecialFolder)];
        foreach (var type in types)
        {
            var spelling = speller.Spell(RuntimeSymbolImporter.Import(type));
            Assert.AreEqual(type, TypeParser.Parse(spelling, session.State.Context), spelling);
        }

        Assert.AreEqual("int32", speller.Spell(RuntimeSymbolImporter.Import(typeof(int))));
        Assert.AreEqual("Console", speller.Spell(RuntimeSymbolImporter.Import(typeof(Console))));
    }

    /// <summary>
    /// Ambiguous short names are qualified while generic and quoted declaration names preserve their spelling.
    /// </summary>
    [TestMethod]
    public void SessionTypeSpellings_DisambiguateAndEscape()
    {
        var session = new Session();
        foreach (var header in new[] { ".class public A.Item { }", ".class public B.Item { }",
            ".class public 'Slash\\\\Name'<T> { }", ".class public 'Quote\\'Name'<T> { }" })
        {
            session.AddLine(header);
        }

        using var snapshot = BindingSnapshot.Capture(session);
        var speller = new TypeSpeller(new SnapshotBindingScope(snapshot));
        foreach (var family in session.Types)
        {
            var type = family.RuntimeType!;
            var spelling = speller.Spell(RuntimeSymbolImporter.Import(type));
            Assert.AreEqual(type, TypeParser.Parse(spelling, session.State.Context), spelling);
            if (type.Name == "Item")
            {
                Assert.AreEqual(type.FullName, spelling);
            }
        }
    }

    /// <summary>
    /// Method overloads, constructors and generic definition tokens resolve to the selected runtime member.
    /// </summary>
    [TestMethod]
    public void MethodSpellings_SelectTheirRuntimeMembers()
    {
        var session = new Session();
        using var snapshot = BindingSnapshot.Capture(session);
        var scope = new SnapshotBindingScope(snapshot);
        var speller = new MemberSpeller(scope);
        MethodBase[] methods = [typeof(Console).GetMethod(nameof(Console.WriteLine), [typeof(string)])!,
            typeof(string).GetMethod(nameof(string.Substring), [typeof(int), typeof(int)])!,
            typeof(Exception).GetConstructor([typeof(string)])!,
            typeof(List<string>).GetMethod(nameof(List<string>.Add))!,
            typeof(Array).GetMethod(nameof(Array.Empty))!,
            typeof(string).GetMethod(nameof(string.Concat), [typeof(string[])])!];
        foreach (var method in methods)
        {
            var target = RuntimeSymbolImporter.Import(method);
            var owner = method.IsConstructor ? "newobj" : method.IsGenericMethodDefinition ? "ldtoken method" : "call";
            var text = speller.TrySpell(target, CompletionSite.None with { Owner = owner });
            Assert.IsNotNull(text, method.ToString());
            var actual = MemberResolver.ResolveMethod(text, session.State.Context, method.IsConstructor);
            Assert.AreEqual(method, actual.Method, text);
        }
    }

    /// <summary>
    /// A field token keeps its declared type and quoted member name through runtime resolution.
    /// </summary>
    [TestMethod]
    public void FieldSpelling_SelectsTheRuntimeField()
    {
        var session = new Session();
        using var snapshot = BindingSnapshot.Capture(session);
        var field = typeof(string).GetField(nameof(string.Empty))!;
        var speller = new MemberSpeller(new SnapshotBindingScope(snapshot));
        var text = speller.TrySpell(RuntimeSymbolImporter.Import(field),
            CompletionSite.None with { Owner = "ldsfld", ReturnTypeText = "string" });
        Assert.IsNotNull(text);
        Assert.AreEqual("string string::Empty", text);
        Assert.AreEqual(field, MemberResolver.ResolveField(text, session.State.Context));
    }

    /// <summary>
    /// Confirmation cannot create a new nested placeholder or method on the open declaration.
    /// </summary>
    [TestMethod]
    public void Confirmation_DoesNotDeclareMissingMembers()
    {
        var session = new Session();
        session.AddLine(".class public Outer {");
        using var snapshot = BindingSnapshot.Capture(session);
        var scope = new SnapshotBindingScope(snapshot);
        var confirmation = scope.ForConfirmation();
        var count = snapshot.Types.Declarations.Count;
        Assert.ThrowsExactly<ReplException>(() =>
            SymbolBinder.BindType(CilSyntaxParser.ParseType("Outer/Absent"), confirmation));
        Assert.ThrowsExactly<ReplException>(() => SymbolBinder.BindMethodReference(
            CilSyntaxParser.ParseMethodReference("void Outer::Absent()"), confirmation, false));
        Assert.HasCount(count, snapshot.Types.Declarations);
        var owner = SymbolBinder.BindType(CilSyntaxParser.ParseType("Outer"), scope).Type;
        Assert.IsTrue(scope.TryGetDeclaration(owner, out var members));
        Assert.IsEmpty(members.Methods);
    }
}
