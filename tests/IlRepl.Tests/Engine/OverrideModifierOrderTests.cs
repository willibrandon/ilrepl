using System.Runtime.Loader;
using IlRepl.Engine;
using Mono.Cecil;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Class-level override mappings retain and enforce the complete implementing signature.
/// </summary>
[TestClass]
public sealed class OverrideModifierOrderTests
{
    private const string Volatile = "[System.Runtime]System.Runtime.CompilerServices.IsVolatile";
    private const string Long = "[System.Runtime.CompilerServices.VisualC]System.Runtime.CompilerServices.IsLong";
    private const string Cdecl = "[System.Runtime]System.Runtime.CompilerServices.CallConvCdecl";
    private static readonly string ReturnType = $"int32 modreq({Volatile}) modopt({Long}) modreq({Cdecl})";
    private static readonly string ParameterType = $"int32 modopt({Cdecl}) modreq({Volatile}) modopt({Long})";
    private static readonly string[] ReturnModifiers = ["modreq:IsVolatile", "modopt:IsLong", "modreq:CallConvCdecl"];
    private static readonly string[] ParameterModifiers = ["modopt:CallConvCdecl", "modreq:IsVolatile", "modopt:IsLong"];

    /// <summary>
    /// An explicit mapping keeps modifier order in its body reference, live metadata, listing, and export.
    /// </summary>
    [TestMethod]
    public void ClassOverride_InterleavedModifiers_PreserveTheirOrderEverywhere()
    {
        var session = IlLines.Load(
            ".class public interface abstract ITransform {",
            $".method public instance abstract virtual {ReturnType} Transform({ParameterType} value) {{ }}",
            "}",
            ".class public Transform implements ITransform {",
            $".override method instance {ReturnType} ITransform::Transform({ParameterType}) "
                + $"with method instance {ReturnType} Transform::Apply({ParameterType})",
            ".method public instance void .ctor() { ldarg.0; call instance void [System.Runtime]System.Object::.ctor(); ret }",
            $".method public instance virtual {ReturnType} Apply({ParameterType} value) {{ ldarg value; ret }}",
            "}");

        var family = session.Types.Single(type => type.FullName == "Transform");
        AssertOverride(family.Definition!.Image!);
        var contract = session.Types.Single(type => type.FullName == "ITransform").RuntimeType!;
        var value = Activator.CreateInstance(family.RuntimeType!);
        Assert.AreEqual(7, contract.GetMethod("Transform")!.Invoke(value, [7]));

        var il = session.ToIlAsm();
        Assert.Contains($".override method instance {ReturnType} ITransform::Transform({ParameterType}) "
            + $"with method instance {ReturnType} Transform::Apply({ParameterType})", il);
        AssertRuns(IlasmLocator.Assemble(il));
        AssertOverride(AssemblyExporter.Write(session, "override-modifier-order"));
    }

    /// <summary>
    /// A body reference with the same modifier sets in another order cannot claim the virtual slot.
    /// </summary>
    [TestMethod]
    public void ClassOverride_DifferentModifierOrder_IsRejected()
    {
        var session = IlLines.Load(
            ".class public interface abstract ITransform {",
            $".method public instance abstract virtual {ReturnType} Transform({ParameterType} value) {{ }}",
            "}",
            ".class public Transform implements ITransform {");
        var reordered = $"int32 modopt({Long}) modreq({Volatile}) modreq({Cdecl})";

        var exception = Assert.ThrowsExactly<ReplException>(() => session.AddLine(
            $".override method instance {ReturnType} ITransform::Transform({ParameterType}) "
                + $"with method instance {reordered} Transform::Apply({ParameterType})"));

        Assert.Contains("does not match", exception.Message);
        Assert.Contains("int32 modreq(IsVolatile) modopt(IsLong) modreq(CallConvCdecl)", exception.Message);
        Assert.Contains("int32 modopt(IsLong) modreq(IsVolatile) modreq(CallConvCdecl)", exception.Message);
    }

    /// <summary>
    /// Replacing a session modifier type rebuilds an explicit mapping against its new identity and exports it.
    /// </summary>
    [TestMethod]
    public void ClassOverride_ModifierTypeReplacement_RebuildsAndExportsTheMapping()
    {
        var returnType = $"int32 modreq({Volatile}) modopt(Marker) modreq({Long})";
        var parameterType = $"int32 modopt({Long}) modreq(Marker) modopt({Volatile})";
        var session = IlLines.Load(
            ".class public Marker { }",
            ".class public interface abstract ITransform {",
            $".method public instance abstract virtual {returnType} Transform({parameterType} value) {{ }}",
            "}",
            ".class public Transform implements ITransform {",
            $".override method instance {returnType} ITransform::Transform({parameterType}) "
                + $"with method instance {returnType} Transform::Apply({parameterType})",
            ".method public instance void .ctor() { ldarg.0; call instance void [System.Runtime]System.Object::.ctor(); ret }",
            $".method public instance virtual {returnType} Apply({parameterType} value) {{ ldarg value; ret }}",
            "}");
        var previousContract = session.Types.Single(type => type.FullName == "ITransform").RuntimeType;
        var previousImplementation = session.Types.Single(type => type.FullName == "Transform").RuntimeType;

        session.AddLine(".class public Marker {");
        session.AddLine(".field public int32 Generation");
        var result = session.AddLine("}");

        Assert.Contains("rebuilt interface ITransform", result.Message!);
        Assert.Contains("and class Transform", result.Message!);
        var marker = session.Types.Single(type => type.FullName == "Marker").RuntimeType!;
        var contract = session.Types.Single(type => type.FullName == "ITransform").RuntimeType!;
        var implementation = session.Types.Single(type => type.FullName == "Transform");
        Assert.AreNotSame(previousContract, contract);
        Assert.AreNotSame(previousImplementation, implementation.RuntimeType);
        Assert.AreSame(marker, implementation.RuntimeType!.GetMethod("Apply")!.ReturnParameter
            .GetOptionalCustomModifiers().Single());
        Assert.AreEqual(7, contract.GetMethod("Transform")!.Invoke(
            Activator.CreateInstance(implementation.RuntimeType), [7]));

        var returnModifiers = new[] { "modreq:IsVolatile", "modopt:Marker", "modreq:IsLong" };
        var parameterModifiers = new[] { "modopt:IsLong", "modreq:Marker", "modopt:IsVolatile" };
        AssertOverride(implementation.Definition!.Image!, returnModifiers, parameterModifiers);
        var exported = AssemblyExporter.Write(session, "override-modifier-replacement");
        AssertOverride(exported, returnModifiers, parameterModifiers);
        AssertRuns(exported);
    }

    private static void AssertRuns(byte[] image)
    {
        var context = new AssemblyLoadContext("override-modifier-order", isCollectible: true);
        try
        {
            var assembly = context.LoadFromStream(new MemoryStream(image));
            var contract = assembly.GetType("ITransform")!;
            var value = Activator.CreateInstance(assembly.GetType("Transform")!);
            Assert.AreEqual(7, contract.GetMethod("Transform")!.Invoke(value, [7]));
        }
        finally
        {
            context.Unload();
        }
    }

    private static void AssertOverride(byte[] image) => AssertOverride(image, ReturnModifiers, ParameterModifiers);

    private static void AssertOverride(
        byte[] image,
        IReadOnlyList<string> returnModifiers,
        IReadOnlyList<string> parameterModifiers)
    {
        using var assembly = AssemblyDefinition.ReadAssembly(new MemoryStream(image));
        var implementation = assembly.MainModule.GetType("Transform").Methods.Single(method => method.Name == "Apply");
        AssertModifiers(implementation.ReturnType, returnModifiers);
        AssertModifiers(implementation.Parameters.Single().ParameterType, parameterModifiers);
        var target = implementation.Overrides.Single();
        AssertModifiers(target.ReturnType, returnModifiers);
        AssertModifiers(target.Parameters.Single().ParameterType, parameterModifiers);
    }

    private static void AssertModifiers(TypeReference type, IReadOnlyList<string> expected)
    {
        var actual = new List<string>();
        while (type is IModifierType modifier)
        {
            actual.Add((modifier is RequiredModifierType ? "modreq:" : "modopt:") + modifier.ModifierType.Name);
            type = modifier.ElementType;
        }

        actual.Reverse();
        Assert.AreSequenceEqual(expected, actual);
    }
}
