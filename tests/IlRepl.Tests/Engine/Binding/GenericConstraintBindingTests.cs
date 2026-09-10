using IlRepl.Engine;
using IlRepl.Engine.Binding;

namespace IlRepl.Tests.Engine.Binding;

/// <summary>
/// Generic argument eligibility agrees with CLR construction while the preview itself reads symbols only.
/// </summary>
[TestClass]
public sealed class GenericConstraintBindingTests
{
    /// <summary>
    /// Generic methods on uncreated session types enforce their constraints through the same binder in both scopes.
    /// </summary>
    [TestMethod]
    public void OpenGenericMethod_ValidatesItsArguments()
    {
        var session = new Session();
        foreach (var line in new[] { ".class public Host {", ".method public static void Touch<valuetype T>() {",
            "ret", "}", ".method public static void Caller() {" })
        {
            session.AddLine(line);
        }

        const string invalid = "void Host::Touch<string>()";
        Assert.ThrowsExactly<ReplException>(() => MemberResolver.ResolveMethod(invalid, session.State.Context, false));
        using var snapshot = BindingSnapshot.Capture(session);
        var scope = new SnapshotBindingScope(snapshot);
        Assert.ThrowsExactly<ReplException>(() =>
            SymbolBinder.BindMethodReference(CilSyntaxParser.ParseMethodReference(invalid), scope, false));
        var valid = SymbolBinder.BindMethodReference(CilSyntaxParser.ParseMethodReference("void Host::Touch<int32>()"), scope, false);
        Assert.AreEqual(TypeSymbol.Primitive("int32"), valid.Method.GenericArguments.Single());
    }

    /// <summary>
    /// A constraint targeting another parameter requires identity or a proven chain, matching runtime construction.
    /// </summary>
    [TestMethod]
    public void ParameterConstraint_AgreesWithRuntime()
    {
        var parameters = typeof(ConstraintArguments<,,,>).GetGenericArguments();
        var definition = RuntimeSymbolImporter.Import(typeof(DependentConstraint<,>));
        using var snapshot = BindingSnapshot.Capture(new Session());
        var scope = new SnapshotBindingScope(snapshot);
        var constraint = scope.GenericParameterDeclarations(definition)[1];
        foreach (var argument in parameters.Append(typeof(Stream)))
        {
            bool expected;
            try
            {
                typeof(DependentConstraint<,>).MakeGenericType(parameters[0], argument);
                expected = true;
            }
            catch (ArgumentException)
            {
                expected = false;
            }

            TypeSymbol[] arguments = [RuntimeSymbolImporter.Import(parameters[0]), RuntimeSymbolImporter.Import(argument)];
            var actual = GenericConstraints.Satisfies(constraint, arguments[1],
                type => SymbolRelations.SubstituteTypeParameters(type, definition.Definition, arguments), scope);
            Assert.AreEqual(expected, actual, argument.Name);
        }
    }

    /// <summary>
    /// Open parameters permitting byref-like arguments remain legal where the CLR accepts the open construction.
    /// </summary>
    [TestMethod]
    public void RefLikeParameter_AgreesWithRuntime()
    {
        var argument = typeof(RefLikeConstraintArgument<>).GetGenericArguments()[0];
        Assert.AreEqual(argument, typeof(List<>).MakeGenericType(argument).GetGenericArguments()[0]);
        using var snapshot = BindingSnapshot.Capture(new Session());
        var scope = new SnapshotBindingScope(snapshot);
        var slot = scope.GenericParameterDeclarations(RuntimeSymbolImporter.Import(typeof(List<>)))[0];
        Assert.IsTrue(GenericConstraints.Satisfies(slot, RuntimeSymbolImporter.Import(argument), type => type, scope));
        Assert.ThrowsExactly<ArgumentException>(() => typeof(List<>).MakeGenericType(typeof(Span<int>)));
        Assert.IsFalse(GenericConstraints.Satisfies(slot, RuntimeSymbolImporter.Import(typeof(Span<int>)), type => type, scope));
    }

    /// <summary>
    /// A private struct constructor retains the implicit default required by new(), matching the runtime's actual check.
    /// </summary>
    [TestMethod]
    public void PrivateStructConstructor_RetainsImplicitDefault()
    {
        var session = new Session();
        foreach (var line in new[] { ".class public sequential NoDefault extends System.ValueType {",
            ".method private specialname rtspecialname instance void .ctor() {", "ret", "}", "}" })
        {
            session.AddLine(line);
        }

        var argument = session.Types.Single().RuntimeType!;
        var constructed = typeof(ConstructorConstraint<>).MakeGenericType(argument);
        Assert.AreEqual(argument, constructed.GetGenericArguments()[0]);
        using var snapshot = BindingSnapshot.Capture(session);
        var scope = new SnapshotBindingScope(snapshot);
        var parameter = scope.GenericParameterDeclarations(
            RuntimeSymbolImporter.Import(typeof(ConstructorConstraint<>)))[0];
        Assert.IsTrue(GenericConstraints.Satisfies(parameter, RuntimeSymbolImporter.Import(argument), type => type, scope));
    }

    /// <summary>
    /// Primary reference constraints propagate through parameters while the class flag alone does not.
    /// </summary>
    [TestMethod]
    public void ReferenceConstraint_AgreesWithRuntimeForParameterChains()
    {
        var arguments = typeof(ConstraintArguments<,,,>).GetGenericArguments();
        using var snapshot = BindingSnapshot.Capture(new Session());
        var scope = new SnapshotBindingScope(snapshot);
        var definition = RuntimeSymbolImporter.Import(typeof(ReferenceConstraint<>));
        var parameter = scope.GenericParameterDeclarations(definition)[0];
        foreach (var argument in arguments)
        {
            bool expected;
            try
            {
                typeof(ReferenceConstraint<>).MakeGenericType(argument);
                expected = true;
            }
            catch (ArgumentException)
            {
                expected = false;
            }

            var actual = GenericConstraints.Satisfies(parameter, RuntimeSymbolImporter.Import(argument), type => type, scope);
            Assert.AreEqual(expected, actual, argument.Name);
        }
    }

    /// <summary>
    /// Invalid concrete generic arguments are refused before they can alter speculative local state.
    /// </summary>
    [TestMethod]
    [DataRow("List<void>")]
    [DataRow("List<typedref>")]
    [DataRow("List<System.TypedReference>")]
    [DataRow("List<typedref modopt(int32)>")]
    [DataRow("List<int32&>")]
    [DataRow("List<System.Span<int32>>")]
    public void InvalidArgument_IsRefusedByTheSharedBinder(string text)
    {
        using var snapshot = BindingSnapshot.Capture(new Session());
        var scope = new SnapshotBindingScope(snapshot);
        Assert.Throws<ReplException>(() => SymbolBinder.BindType(CilSyntaxParser.ParseType(text), scope));
    }

    /// <summary>
    /// Typed references remain invalid generic arguments even for parameters that permit byref-like types.
    /// </summary>
    [TestMethod]
    public void TypedReference_AgreesWithRuntimeRejection()
    {
        using var snapshot = BindingSnapshot.Capture(new Session());
        var scope = new SnapshotBindingScope(snapshot);
        foreach (var type in new[] { typeof(List<>), typeof(RefLikeConstraintArgument<>) })
        {
            Assert.IsTrue(RuntimeRejects(() => type.MakeGenericType(typeof(TypedReference))), type.Name);
            var parameter = scope.GenericParameterDeclarations(RuntimeSymbolImporter.Import(type))[0];
            Assert.IsFalse(GenericConstraints.Satisfies(parameter, RuntimeSymbolImporter.Import(typeof(TypedReference)),
                argument => argument, scope));
        }

        var method = typeof(Array).GetMethod(nameof(Array.Empty))!;
        Assert.IsTrue(RuntimeRejects(() => method.MakeGenericMethod(typeof(TypedReference))));
        var definition = RuntimeSymbolImporter.Import(method);
        Assert.IsFalse(GenericConstraints.SatisfiesMethod(definition, definition.DeclaringType,
            [TypeSymbol.Primitive("typedref")], scope));
    }

    /// <summary>
    /// Named generic definitions and constructed arguments retain the same byref-like fact as runtime reflection.
    /// </summary>
    [TestMethod]
    public void ByRefLikeFlag_AgreesWithRuntime()
    {
        using var snapshot = BindingSnapshot.Capture(new Session());
        var scope = new SnapshotBindingScope(snapshot);
        var definition = scope.LookupType("System.Span`1", null, 0, false).Type;
        Assert.IsTrue(definition.IsByRefLike);
        Assert.AreEqual(typeof(Span<int>).IsByRefLike, TypeSymbol.Construct(definition, [TypeSymbol.Primitive("int32")]).IsByRefLike);
        Assert.IsFalse(scope.LookupType("System.Memory`1", null, 0, false).Type.IsByRefLike);
    }

    private static bool RuntimeRejects(Action construct)
    {
        try
        {
            construct();
            return false;
        }
        catch (Exception exception) when (exception is ArgumentException or TypeLoadException or BadImageFormatException)
        {
            return true;
        }
    }

}
