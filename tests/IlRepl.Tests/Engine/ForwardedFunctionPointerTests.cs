using System.Runtime.Loader;
using IlRepl.Engine;
using Mono.Cecil;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Private generic aliases preserve function-pointer signatures through enclosing owners and exported assemblies.
/// </summary>
[TestClass]
public sealed class ForwardedFunctionPointerTests
{
    /// <summary>
    /// Hoisted owner arguments cannot replace method arguments inside function-pointer returns or parameters.
    /// </summary>
    [TestMethod]
    public void Alias_NestedGenericFunctionPointersRetainMethodArguments()
    {
        var session = IlLines.Load(
            ".class public Outer {",
            ".class nested private Hidden`1<T> {",
            ".method private static method !!U *(!!U) Read<U>(method !!U *(!!U) first, method !!U *(!!U) second) {",
            "ldarg.0", "ret", "}", "}", "}");
        var edit = session.PrepareEdit("method !!0 *(!!0) Outer/Hidden`1<int64>::Read<int32>"
            + "(method !!0 *(!!0), method !!0 *(!!0))", "Copy");
        session.CommitEdit(edit.Name, edit.Source.Replace("ldarg.0", "ldarg.1", StringComparison.Ordinal));
        foreach (var line in IlLines.Expand(
            ".method int32 First(int32 value) { ldarg.0; ldc.i4.1; add; ret }",
            ".method int32 Second(int32 value) { ldarg.0; ldc.i4.2; add; ret }",
            ".method int32 Scenario() { ldc.i4.s 40; ldftn First; ldftn Second; call Copy; calli int32(int32); ret }"))
        {
            session.AddLine(line);
        }

        session.AddLine("call Scenario");
        foreach (var image in new[] { AssemblyExporter.Write(session, "forwarded-pointers"), IlasmLocator.Assemble(session.ToIlAsm()) })
        {
            using var module = ModuleDefinition.ReadModule(new MemoryStream(image));
            var root = module.Types.Single(type => type.Name == edit.Method!.DeclaringType!.DeclaringType!.Name);
            var forwarding = root.Methods.Single(method => method.GenericParameters.Count == 2);
            AssertPointer(forwarding.ReturnType, 1);
            foreach (var parameter in forwarding.Parameters)
            {
                AssertPointer(parameter.ParameterType, 1);
            }

            var context = new AssemblyLoadContext("forwarded-pointers-" + Guid.NewGuid(), isCollectible: true);
            try
            {
                var assembly = context.LoadFromStream(new MemoryStream(image));
                Assert.AreEqual(42, assembly.GetType("IlRepl.Cell")!.GetMethod("Scenario")!.Invoke(null, null));
                Assert.AreEqual(42, assembly.GetType("IlRepl.Cell")!.GetMethod("Run")!.Invoke(null, null));
            }
            finally
            {
                context.Unload();
            }
        }

        Assert.AreEqual(42, session.Run().Value);
    }

    private static void AssertPointer(TypeReference type, int position)
    {
        var pointer = (FunctionPointerType)type;
        Assert.AreEqual(MethodCallingConvention.Default, pointer.CallingConvention);
        Assert.IsFalse(pointer.HasThis);
        Assert.IsFalse(pointer.ExplicitThis);
        var result = (GenericParameter)pointer.ReturnType;
        var parameter = (GenericParameter)pointer.Parameters.Single().ParameterType;
        Assert.AreEqual(GenericParameterType.Method, result.Type);
        Assert.AreEqual(position, result.Position);
        Assert.AreEqual(GenericParameterType.Method, parameter.Type);
        Assert.AreEqual(position, parameter.Position);
    }
}
