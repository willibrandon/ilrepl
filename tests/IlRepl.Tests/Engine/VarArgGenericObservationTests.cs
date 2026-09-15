using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using IlRepl.Engine;
using IlRepl.Host;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Optional vararg signatures retain the generic context of each real caller through comparison emission.
/// </summary>
[TestClass]
public sealed class VarArgGenericObservationTests
{
    /// <summary>
    /// Supplies cancellation for real comparison processes on runtimes supporting managed varargs.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Caller type and method parameters with identical names remain distinct inside exact optional signatures and constraints.
    /// </summary>
    /// <param name="shape">The optional signature surrounding each caller generic parameter.</param>
    [TestMethod]
    [DataRow("scalar")]
    [DataRow("array")]
    [DataRow("modifier")]
    [DataRow("byref")]
    [DataRow("pointer")]
    [DataRow("function-pointer")]
    public void Export_VarargWrappersPreserveCallerGenericContexts(string shape)
    {
        var writer = new CecilWriter("generic-vararg-contexts");
        var owner = new TypeDefinition("N", "Target", TypeAttributes.Public, writer.Object);
        writer.Module.Types.Add(owner);
        VarArgEditAliasTests.DefineCounter(writer.Module, owner);
        var target = owner.Methods.Single();
        var entry = ComparisonInstrumentation.Wrap(writer, target);
        foreach (var valueType in new[] { false, true })
        {
            var caller = new TypeDefinition("N", valueType ? "ValueCaller`1" : "ReferenceCaller`1", TypeAttributes.Public, writer.Object);
            writer.Module.Types.Add(caller);
            var typeParameter = new GenericParameter("T", caller)
            {
                Attributes = valueType ? GenericParameterAttributes.NotNullableValueTypeConstraint
                    | GenericParameterAttributes.DefaultConstructorConstraint : GenericParameterAttributes.ReferenceTypeConstraint,
            };
            caller.GenericParameters.Add(typeParameter);
            var method = new MethodDefinition("Run", MethodAttributes.Public | MethodAttributes.Static, writer.Module.TypeSystem.Int32);
            caller.Methods.Add(method);
            var methodParameter = new GenericParameter("T", method) { Attributes = GenericParameterAttributes.ReferenceTypeConstraint };
            method.GenericParameters.Add(methodParameter);
            var enumerable = new GenericInstanceType(writer.Import(typeof(IEnumerable<>)));
            enumerable.GenericArguments.Add(typeParameter);
            methodParameter.Constraints.Add(new GenericParameterConstraint(enumerable));
            var il = method.Body.GetILProcessor();
            foreach (var generic in new[] { typeParameter, methodParameter })
            {
                var optional = Optional(writer, shape, generic);
                var argument = new ParameterDefinition(optional);
                method.Parameters.Add(argument);
                var call = new MethodReference(entry.Name, entry.ReturnType, owner)
                {
                    CallingConvention = MethodCallingConvention.VarArg,
                };
                call.Parameters.Add(new ParameterDefinition(writer.Module.TypeSystem.Int32));
                call.Parameters.Add(new ParameterDefinition(new SentinelType(optional)));
                il.Emit(OpCodes.Ldc_I4, 41);
                il.Emit(OpCodes.Ldarg, argument);
                il.Emit(OpCodes.Call, call);
                if (generic == typeParameter)
                {
                    il.Emit(OpCodes.Pop);
                }
            }

            il.Emit(OpCodes.Ret);
        }

        ComparisonInstrumentation.Complete(writer, target, entry);
        var image = writer.Write();
        using var module = ModuleDefinition.ReadModule(new MemoryStream(image));
        foreach (var caller in module.Types.Where(type => type.Name.EndsWith("Caller`1", StringComparison.Ordinal)))
        {
            var method = caller.Methods.Single();
            var calls = method.Body.Instructions.Select(instruction => instruction.Operand).OfType<GenericInstanceMethod>().ToArray();
            Assert.HasCount(2, calls);
            Assert.AreNotEqual(calls[0].Resolve(), calls[1].Resolve());
            foreach (var (call, position) in calls.Select((call, position) => (call, position)))
            {
                Assert.HasCount(2, call.GenericArguments);
                Assert.AreSame(caller, ((GenericParameter)call.GenericArguments[0]).Owner);
                Assert.AreSame(method, ((GenericParameter)call.GenericArguments[1]).Owner);
                var wrapper = call.Resolve();
                Assert.AreNotSame(caller, wrapper.DeclaringType);
                Assert.HasCount(2, wrapper.GenericParameters);
                Assert.AreEqual(caller.GenericParameters[0].Attributes, wrapper.GenericParameters[0].Attributes);
                var constraint = (GenericInstanceType)wrapper.GenericParameters[1].Constraints.Single().ConstraintType;
                Assert.AreSame(wrapper.GenericParameters[0], constraint.GenericArguments.Single());
                var parameter = Parameter(wrapper.Parameters[1].ParameterType);
                Assert.AreSame(wrapper, parameter.Owner);
                Assert.AreEqual(position, parameter.Position);
                Assert.AreEqual(GenericParameterType.Method, parameter.Type);
                var forwarded = wrapper.Body.Instructions.Select(instruction => instruction.Operand).OfType<MethodReference>()
                    .Single(reference => reference.Name == "Read");
                Assert.IsFalse(forwarded is GenericInstanceMethod);
                Assert.AreEqual(MethodCallingConvention.VarArg, forwarded.CallingConvention);
                var sentinel = (SentinelType)forwarded.Parameters[1].ParameterType;
                Assert.AreEqual(position, Parameter(sentinel.ElementType).Position);
                Assert.AreEqual(GenericParameterType.Method, Parameter(sentinel.ElementType).Type);
            }
        }

        var context = new AssemblyLoadContext("generic-vararg-contexts", isCollectible: true);
        try
        {
            var assembly = context.LoadFromStream(new MemoryStream(image));
            foreach (var type in assembly.GetTypes())
            {
                foreach (var method in type.GetMethods())
                {
                    _ = method.GetParameters();
                    _ = method.ReturnType;
                }
            }
        }
        finally
        {
            context.Unload();
        }
    }

    /// <summary>
    /// Session syntax and both export formats retain caller-owned optional parameters before platform-specific execution.
    /// </summary>
    /// <param name="invokeGeneric">Whether the scenario invokes the generic caller.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Export_ParsedGenericVarargCallerPreservesOptionalSignatures(bool invokeGeneric)
    {
        var session = new Session();
        var (_, _, owner) = CecilFixture.Build(VarArgEditAliasTests.DefineCounter, session.Resolver);
        session.TypeTable.MethodAliases.Add("Copy", owner.GetMethod("Read")!);
        session.ClearCell();
        AddScenario(session, invokeGeneric, declareScenario: false);
        foreach (var image in new[] { AssemblyExporter.Write(session, "parsed-generic-varargs"),
            IlasmLocator.Assemble(session.ToIlAsm()) })
        {
            using var module = ModuleDefinition.ReadModule(new MemoryStream(image));
            var caller = module.Types.Single(type => type.Name == "Caller`1");
            var method = caller.Methods.Single(member => member.Name == "Run");
            var calls = method.Body.Instructions.Select(instruction => instruction.Operand).OfType<MethodReference>().ToArray();
            Assert.HasCount(2, calls);
            foreach (var (call, index) in calls.Select((call, index) => (call, index)))
            {
                Assert.AreEqual(MethodCallingConvention.VarArg, call.CallingConvention);
                var parameter = (GenericParameter)((SentinelType)call.Parameters[1].ParameterType).ElementType;
                Assert.AreEqual(0, parameter.Position);
                Assert.AreEqual(index == 0 ? GenericParameterType.Type : GenericParameterType.Method, parameter.Type);
            }
        }
    }

    /// <summary>
    /// Generic vararg callers remain valid whether the selected scenario invokes them or leaves them unused.
    /// </summary>
    /// <param name="invokeGeneric">Whether the scenario invokes the generic caller.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [OSCondition(ConditionMode.Include, OperatingSystems.Windows)]
    public async Task Compare_GenericVarargCallerPreservesOptionalInputs(bool invokeGeneric)
    {
        var session = new Session();
        var (assembly, _, _) = CecilFixture.Build(VarArgEditAliasTests.DefineCounter, session.Resolver);
        var edit = session.PrepareEdit("vararg int32 [" + assembly.GetName().Name + "]N.Fixture::Read(int32)", "Copy");
        session.CommitEdit(edit.Name, edit.Source.Replace("ret", "ldc.i4.1\nadd\nret", StringComparison.Ordinal));
        AddScenario(session, invokeGeneric);

        var result = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy using Scenario"),
            TestContext.CancellationToken);
        Assert.AreEqual("different", result.Outcome, result.Original.Detail + "; " + result.Edited.Detail);
        Assert.AreEqual(invokeGeneric ? "42" : "41", result.Original.Result!.Value);
        Assert.AreEqual(invokeGeneric ? "43" : "42", result.Edited.Result!.Value);
        foreach (var side in new[] { result.Original, result.Edited })
        {
            Assert.HasCount(invokeGeneric ? 2 : 1, side.Invocations);
            if (invokeGeneric)
            {
                Assert.AreEqual("input", side.Invocations[0].Inputs.Single(input => input.Name == "argument 1").Value.Value);
                Assert.AreEqual("7", side.Invocations[1].Inputs.Single(input => input.Name == "argument 1").Value.Value);
            }
        }
    }

    private static void AddScenario(Session session, bool invokeGeneric, bool declareScenario = true)
    {
        const string caller = """
            .class public Caller`1<T> {
              .method public static int32 Run<U>(!T first, !!U second) {
                ldc.i4.s 41
                ldarg.0
                call vararg int32 Copy(int32, ..., !T)
                pop
                ldc.i4.s 41
                ldarg.1
                call vararg int32 Copy(int32, ..., !!U)
                ret
              }
            }
            """;
        var invocation = invokeGeneric ? """
            ldstr "input"
            ldc.i4.7
            call int32 Caller`1<string>::Run<int32>(!0, !!0)
            """ : "ldc.i4.s 41\ncall vararg int32 Copy(int32)";
        var source = caller + "\n" + (declareScenario ? ".method int32 Scenario() {\n" : "") + invocation
            + "\nret" + (declareScenario ? "\n}" : "");
        foreach (var line in source.Split('\n'))
        {
            session.AddLine(line);
        }
    }

    private static TypeReference Optional(CecilWriter writer, string shape, TypeReference parameter)
    {
        if (shape == "function-pointer")
        {
            var pointer = new FunctionPointerType { ReturnType = parameter };
            pointer.Parameters.Add(new ParameterDefinition(parameter));
            return pointer;
        }

        return shape switch
        {
            "array" => new ArrayType(parameter, 2),
            "modifier" => new OptionalModifierType(writer.Import(typeof(IsLong)), parameter),
            "byref" => new ByReferenceType(parameter),
            "pointer" => new PointerType(parameter),
            _ => parameter,
        };
    }

    private static GenericParameter Parameter(TypeReference type) => type switch
    {
        GenericParameter parameter => parameter,
        FunctionPointerType pointer => Parameter(pointer.ReturnType),
        TypeSpecification specification => Parameter(specification.ElementType),
        _ => throw new AssertFailedException("The optional signature lost its generic parameter."),
    };
}
