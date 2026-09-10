using System.Reflection;
using IlRepl.Engine;
using IlRepl.Engine.Binding;

namespace IlRepl.Tests.Engine.Binding;

/// <summary>
/// Declaration grammar has the same result with runtime bindings and with metadata-only preview symbols.
/// </summary>
[TestClass]
public sealed class DeclarationBindingTests
{
    /// <summary>
    /// A generic method header binds its own parameters without creating a runtime assembly.
    /// </summary>
    [TestMethod]
    public void GenericHeader_SnapshotBindsWithoutRuntimePrototypes()
    {
        var session = new Session();
        using var snapshot = BindingSnapshot.Capture(session);
        var scope = new SnapshotBindingScope(snapshot);
        var owner = TypeHeaderParser.Parse("public Box {", false);
        var identity = DefinitionId.ForDeclaration(-1, 1);
        var thread = Environment.CurrentManagedThreadId;
        var created = new List<string>();
        void Record(object? sender, AssemblyLoadEventArgs args)
        {
            if (Environment.CurrentManagedThreadId == thread && args.LoadedAssembly.IsDynamic)
            {
                created.Add(args.LoadedAssembly.FullName ?? "dynamic assembly");
            }
        }

        AppDomain.CurrentDomain.AssemblyLoad += Record;
        try
        {
        var method = MethodDeclarationParser.ParseMember(
            "public static !!T Id<TUnused, T>(!!T value) {",
            scope,
            owner,
            out var opens,
            out var closes,
            out var parameters,
            names => [.. names.Select((name, i) => TypeSymbol.Parameter(identity, true, i, name, GenericParameterAttributes.None))]);

        Assert.IsTrue(opens);
        Assert.IsFalse(closes);
        Assert.HasCount(2, parameters);
        Assert.AreEqual(parameters[1], method.ReturnType);
        Assert.AreEqual(parameters[1], method.Parameters[0].Type);
        Assert.AreEqual("value", method.Parameters[0].Name);
        }
        finally
        {
            AppDomain.CurrentDomain.AssemblyLoad -= Record;
        }

        Assert.IsEmpty(created);
    }

    /// <summary>
    /// A return modifier and assembly-qualified parameter have the same identities in either binding scope.
    /// </summary>
    [TestMethod]
    public void SessionHeader_SnapshotAndRuntimeAgree()
    {
        var session = new Session();
        const string Header = "int32 modopt([System.Runtime]System.Runtime.CompilerServices.IsLong) "
            + "F([System.Runtime]System.String value) {";
        var runtime = MethodHeaderParser.Parse(Header, session.State.Context, out _);
        using var snapshot = BindingSnapshot.Capture(session);
        var symbol = MethodDeclarationParser.Parse(Header, new SnapshotBindingScope(snapshot), out _);
        Assert.AreEqual(RuntimeSymbolImporter.Import(runtime.ReturnType), symbol.ReturnType);
        Assert.AreEqual(RuntimeSymbolImporter.Import(runtime.Parameters[0].Type), symbol.Parameters[0].Type);
    }

    /// <summary>
    /// A local's leading assembly qualifier reaches the type binder instead of being consumed as a slot index.
    /// </summary>
    [TestMethod]
    public void Locals_AssemblyQualifierIsPreserved()
    {
        var session = new Session();
        session.AddLine(".locals init ([System.Runtime]System.String value)");
        Assert.AreEqual(typeof(string), session.State.Locals[0].Type);
        using var snapshot = BindingSnapshot.Capture(new Session());
        var slots = VariableDeclarationParser.ParseLocals(
            "init ([System.Runtime]System.String value)", new SnapshotBindingScope(snapshot));
        Assert.AreEqual(TypeSymbol.Primitive("string"), slots[0].Type);
        Assert.AreEqual("value", slots[0].Name);
    }

    /// <summary>
    /// A struct argument is described without creating its default value or running its initializer.
    /// </summary>
    [TestMethod]
    public void Arguments_UncreatedStructKeepsAnUnevaluatedDefault()
    {
        var session = new Session();
        session.AddLine(".class public sequential Point extends System.ValueType {");
        session.AddLine(".method public void F() {");
        using var snapshot = BindingSnapshot.Capture(session.State.Context);
        var arguments = VariableDeclarationParser.ParseArguments(
            "(Point point)", new SnapshotBindingScope(snapshot));
        Assert.IsNull(arguments[0].Literal);
        Assert.IsTrue(arguments[0].Type.IsValueTypeShape);
        Assert.AreEqual("point", arguments[0].Name);
    }
}
