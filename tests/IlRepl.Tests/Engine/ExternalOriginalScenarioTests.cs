using IlRepl.Engine;
using IlRepl.Host;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Comparison scenarios retain accessible framework originals whose runtime-owned helpers cannot be copied.
/// </summary>
[TestClass]
public sealed class ExternalOriginalScenarioTests
{
    /// <summary>
    /// The cancellation context for real worker processes.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// An external method retains its closed owner and method arguments when runtime-owned helpers cannot be copied.
    /// </summary>
    /// <param name="argument">The requested owner argument, distinct from the method argument.</param>
    /// <param name="genericMethod">Whether the selected method also requires a method argument.</param>
    [TestMethod]
    [DataRow("int32", false)]
    [DataRow("int64", false)]
    [DataRow("int32", true)]
    [DataRow("int64", true)]
    public async Task Compare_GenericExternalOwnerRemainsClosed(string argument, bool genericMethod)
    {
        var session = new Session();
        var (assembly, _, _) = CecilFixture.Build((module, owner) => DefineGenericMethod(module, owner, genericMethod),
            session.Resolver, "Fixture`1");
        var edit = session.PrepareEdit("int32 [" + assembly.GetName().Name + "]N.Fixture`1<" + argument
            + ">::Read" + (genericMethod ? "<int16>" : "") + "()", "Copy");
        Assert.IsNotEmpty(edit.Baseline.Problems);
        var expected = edit.Original.Requested.Invoke(null, null)!.ToString();
        session.CommitEdit(edit.Name, ".method public static int32 Read" + (genericMethod ? "<U>" : "") + "() {\nldc.i4.0\nret\n}");
        foreach (var line in IlLines.Expand(".method int32 Scenario() { call Copy; ret }"))
        {
            session.AddLine(line);
        }

        var package = ComparisonCapture.Create(session, "Copy using Scenario");
        using (var module = ModuleDefinition.ReadModule(new MemoryStream(package.Original.Image)))
        {
            var call = module.Types.SelectMany(type => type.Methods).Where(method => method.HasBody)
                .SelectMany(method => method.Body.Instructions).Select(instruction => instruction.Operand).OfType<MethodReference>()
                .Single(method => method.Name == "Read" && method.DeclaringType.Scope.Name == assembly.GetName().Name);
            var owner = (GenericInstanceType)call.DeclaringType;
            var parameter = (GenericParameter)owner.GenericArguments.Single();
            Assert.AreEqual(GenericParameterType.Type, parameter.Type);
            Assert.AreEqual(0, parameter.Position);
            Assert.AreEqual(genericMethod, call is GenericInstanceMethod);
            if (call is GenericInstanceMethod generic)
            {
                var methodParameter = (GenericParameter)generic.GenericArguments.Single();
                Assert.AreEqual(GenericParameterType.Method, methodParameter.Type);
                Assert.AreEqual(0, methodParameter.Position);
            }
        }

        var result = await ProcessComparisonRunner.RunAsync(package, TestContext.CancellationToken);

        Assert.AreEqual("different", result.Outcome, result.Original.Detail + "; " + result.Edited.Detail);
        Assert.AreEqual(expected, result.Original.Result!.Value);
        Assert.AreEqual("0", result.Edited.Result!.Value);
        Assert.AreEqual(expected, result.Original.Invocations.Single().Outputs.Single(member => member.Name == "return").Value.Value);
        Assert.AreEqual("0", result.Edited.Invocations.Single().Outputs.Single(member => member.Name == "return").Value.Value);
    }

    private static void DefineGenericMethod(ModuleDefinition module, TypeDefinition owner, bool genericMethod)
    {
        var callback = new TypeDefinition("N", "ReadValue", TypeAttributes.NotPublic | TypeAttributes.Sealed,
            module.ImportReference(typeof(MulticastDelegate)));
        module.Types.Add(callback);
        var constructor = new MethodDefinition(".ctor", MethodAttributes.Public | MethodAttributes.SpecialName
            | MethodAttributes.RTSpecialName, module.TypeSystem.Void) { ImplAttributes = MethodImplAttributes.Runtime };
        constructor.Parameters.Add(new ParameterDefinition(module.TypeSystem.Object));
        constructor.Parameters.Add(new ParameterDefinition(module.TypeSystem.IntPtr));
        callback.Methods.Add(constructor);
        var invoke = new MethodDefinition("Invoke", MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.NewSlot,
            module.TypeSystem.Int32) { ImplAttributes = MethodImplAttributes.Runtime };
        invoke.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int32));
        callback.Methods.Add(invoke);
        var ownerParameter = new GenericParameter("T", owner);
        owner.GenericParameters.Add(ownerParameter);
        var method = new MethodDefinition("Read", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Int32);
        owner.Methods.Add(method);
        var methodParameter = new GenericParameter("U", method);
        if (genericMethod)
        {
            method.GenericParameters.Add(methodParameter);
        }
        var il = method.Body.GetILProcessor();
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ldftn, module.ImportReference(typeof(Math).GetMethod(nameof(Math.Abs), [typeof(int)])!));
        il.Emit(OpCodes.Newobj, constructor);
        il.Emit(OpCodes.Ldc_I4, -42);
        il.Emit(OpCodes.Callvirt, invoke);
        il.Emit(OpCodes.Sizeof, ownerParameter);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Sizeof, genericMethod ? methodParameter : module.TypeSystem.Int16);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// The same scenario reaches the real framework original and the edited implementation in separate runtimes.
    /// </summary>
    /// <returns>The completed original and edited invocation assertions.</returns>
    [TestMethod]
    public async Task Compare_FrameworkOriginalSupportsScenarioCalls()
    {
        var session = new Session();
        var edit = session.PrepareEdit("int32 Math::Abs(int32)", "Copy");
        session.CommitEdit(edit.Name, ".method public static int32 Abs(int32 value) cil managed {\nldarg.0\nret\n}");
        foreach (var line in IlLines.Expand(".method int32 Scenario() { ldc.i4.s -42; call Copy; ret }"))
        {
            session.AddLine(line);
        }

        var result = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy using Scenario"),
            TestContext.CancellationToken);

        Assert.AreEqual("different", result.Outcome, result.Original.Detail + "; " + result.Edited.Detail);
        Assert.AreEqual("completed", result.Original.Outcome);
        Assert.AreEqual("completed", result.Edited.Outcome);
        Assert.AreEqual("42", result.Original.Result!.Value);
        Assert.AreEqual("-42", result.Edited.Result!.Value);
        Assert.HasCount(1, result.Original.Invocations);
        Assert.HasCount(1, result.Edited.Invocations);
        foreach (var side in new[] { result.Original, result.Edited })
        {
            Assert.AreEqual("-42", side.Invocations[0].Inputs.Single(member => member.Name == "argument 0").Value.Value);
        }

        Assert.AreEqual(42, edit.Original.Requested.Invoke(null, [-42]));
        Assert.AreEqual(-42, edit.Method!.Invoke(null, [-42]));
    }
}
