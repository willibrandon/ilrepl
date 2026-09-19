using System.Reflection;
using System.Runtime.Loader;
using IlRepl.Engine;
using IlRepl.Host;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Copied activation keeps external types and preserves the framework's validation and constructor errors.
/// </summary>
public sealed partial class ActivationEditTests
{
    /// <summary>
    /// Activating an external generic definition remaps its copied type argument without changing the external definition.
    /// </summary>
    [TestMethod]
    public async Task Compare_ExternalGenericActivationUsesCopiedArguments()
    {
        var fixture = IlLines.Load(".class public Activation.Owner {",
            ".method public static int32 Read(string assembly, string name) {", "ldarg.0", "ldarg.1",
            "call class System.Runtime.Remoting.ObjectHandle Activator::CreateInstance(string, string)",
            "callvirt instance object System.Runtime.Remoting.ObjectHandle::Unwrap()",
            "callvirt instance class Type Object::GetType()", "ldtoken class List<Activation.Owner>",
            "call class Type Type::GetTypeFromHandle(valuetype RuntimeTypeHandle)", "ceq", "ldc.i4.s 42", "mul", "ret", "}", "}");
        var source = fixture.PrepareEdit("int32 Activation.Owner::Read(string, string)", "Source").Original.Requested;
        Assert.IsTrue(SessionAssemblies.TryGetDefinition(source.Module.Assembly, out var definition));
        var session = new Session();
        session.Resolver.LoadImage(definition.Image!);
        var edit = session.PrepareEdit("int32 Activation.Owner::Read(string, string)", "Copy");
        var assembly = typeof(List<>).Assembly.FullName!;
        var name = "System.Collections.Generic.List`1[[" + edit.Original.Requested.DeclaringType!.AssemblyQualifiedName + "]]";
        using (AssemblyLoadContext.GetLoadContext(edit.Original.Requested.Module.Assembly)!.EnterContextualReflection())
        {
            Assert.AreEqual(42, edit.Original.Requested.Invoke(null, [assembly, name]));
        }

        Assert.IsEmpty(edit.Problems, string.Join('\n', edit.Problems));
        session.CommitEdit(edit.Name, edit.Source);
        Assert.AreEqual(42, edit.Method!.Invoke(null, [assembly, name]));
        var arguments = "(" + LiteralParser.Escape(assembly) + ", " + LiteralParser.Escape(name) + ")";
        var result = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy " + arguments),
            TestContext.CancellationToken);
        Assert.AreEqual("match", result.Outcome, result.Original.Detail + "; " + result.Edited.Detail);
        Assert.AreEqual("42", result.Original.Result!.Value);
        Assert.AreEqual("42", result.Edited.Result!.Value);
        session.AddLine("ldstr " + LiteralParser.Escape(assembly));
        session.AddLine("ldstr " + LiteralParser.Escape(name));
        session.AddLine("call Copy");
        var image = AssemblyExporter.Write(session, "generic-activation");
        var context = new AssemblyLoadContext("generic-activation", isCollectible: true);
        try
        {
            var saved = context.LoadFromStream(new MemoryStream(image));
            Assert.AreEqual(42, saved.GetType("IlRepl.Cell")!.GetMethod("Run")!.Invoke(null, null));
        }
        finally
        {
            context.Unload();
        }
    }

    /// <summary>
    /// External targets, invalid names, missing constructors, and unsupported activation attributes retain their runtime behavior.
    /// </summary>
    [TestMethod]
    public void Invoke_ActivationOptionsRetainRuntimeBehavior()
    {
        var session = IlLines.Load(".class public Activation.Owner {",
            ".method public instance void .ctor() {", "ldarg.0", "call instance void Object::.ctor()", "ret", "}",
            ".method public static object Make(string assembly, string name, object[] attributes) {",
            "ldarg.0", "ldarg.1", "ldarg.2",
            "call class System.Runtime.Remoting.ObjectHandle Activator::CreateInstance(string, string, object[])",
            "dup", "brtrue VALUE", "pop", "ldnull", "ret", "VALUE:",
            "callvirt instance object System.Runtime.Remoting.ObjectHandle::Unwrap()", "ret", "}", "}");
        var edit = session.PrepareEdit("object Activation.Owner::Make(string, string, object[])", "Copy");
        Assert.IsEmpty(edit.Problems, string.Join('\n', edit.Problems));
        session.CommitEdit(edit.Name, edit.Source);
        var original = edit.Original.Requested;
        foreach (var method in new[] { original, edit.OriginalMethod, edit.Method! })
        {
            Assert.AreEqual(method.DeclaringType, method.Invoke(null, [null, "Activation.Owner", null])!.GetType());
            Assert.AreEqual(0, method.Invoke(null, [typeof(int).Assembly.FullName, typeof(int).FullName, null]));
            Assert.IsNull(method.Invoke(null, [typeof(int).Assembly.FullName, typeof(int?).FullName, null]));
        }

        object?[][] invalid =
        [
            [null, null, null], [null, "", null], [null, "Activation.Missing", null], [null, "activation.owner", null],
            [null, original.DeclaringType!.AssemblyQualifiedName, null], ["", "Activation.Owner", null],
            ["missing-activation-assembly", "Activation.Owner", null], ["bad, Version=no", "Activation.Owner", null],
            [new AssemblyName(original.Module.Assembly.FullName!) { Version = new Version(9, 0, 0, 0) }.FullName, "Activation.Owner", null],
            [null, "Activation.Owner", new object[] { "unsupported" }],
            [typeof(string).Assembly.FullName, typeof(string).FullName, null],
        ];
        foreach (var arguments in invalid)
        {
            var expected = Assert.ThrowsExactly<TargetInvocationException>(() => original.Invoke(null, arguments));
            foreach (var method in new[] { edit.OriginalMethod, edit.Method! })
            {
                var actual = Assert.ThrowsExactly<TargetInvocationException>(() => method.Invoke(null, arguments));
                Assert.AreEqual(expected.InnerException!.GetType(), actual.InnerException!.GetType());
            }
        }
    }
}
