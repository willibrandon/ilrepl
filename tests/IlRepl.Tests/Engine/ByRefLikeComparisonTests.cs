using System.Runtime.CompilerServices;
using IlRepl.Engine;
using IlRepl.Host;
using Mono.Cecil;
using Mono.Cecil.Cil;
using RuntimeGenericAttributes = System.Reflection.GenericParameterAttributes;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Imported byref-like values stay usable in scenarios while their unboxable observations remain unavailable.
/// </summary>
[TestClass]
public sealed class ByRefLikeComparisonTests
{
    /// <summary>
    /// Supplies cancellation for actual comparison workers.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// External generic spans can pass by value or reference without introducing an illegal box instruction.
    /// </summary>
    /// <param name="name">The framework span type.</param>
    /// <param name="byReference">Whether the method receives the span by reference.</param>
    /// <returns>The completed scalar result and unavailable argument assertions.</returns>
    [TestMethod]
    [DataRow("Span", false)]
    [DataRow("Span", true)]
    [DataRow("ReadOnlySpan", false)]
    [DataRow("ReadOnlySpan", true)]
    public async Task Run_ImportedSpanArgument_ExecutesWithoutBoxing(string name, bool byReference)
    {
        var span = "valuetype " + name + "<int32>";
        var source = ".method int32 Read(" + span + (byReference ? "&" : "") + " value) {\n"
            + (byReference ? "ldarg.0" : "ldarga 0") + "\ncall instance int32 " + span + "::get_Length()\nret\n}";
        var session = IlLines.Load(source.Split('\n'));
        var edit = session.PrepareEdit("Read", "Copy");
        session.CommitEdit(edit.Name, edit.Source.Replace("ret", "ldc.i4.1\nadd\nret", StringComparison.Ordinal));
        foreach (var line in (".method int32 Scenario() {\n.locals init (" + span + " value)\nldc.i4.3\nnewarr int32\n"
            + "newobj instance void " + span + "::.ctor(int32[])\nstloc.0\n" + (byReference ? "ldloca 0" : "ldloc.0")
            + "\ncall Copy\nret\n}").Split('\n'))
        {
            session.AddLine(line);
        }

        var result = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy using Scenario"),
            TestContext.CancellationToken);

        Assert.AreEqual("incomplete", result.Outcome);
        foreach (var side in new[] { result.Original, result.Edited })
        {
            Assert.AreEqual("completed", side.Outcome, side.Detail);
            Assert.IsNull(side.Exception);
            Assert.AreEqual(side == result.Original ? "3" : "4", side.Result!.Value);
            var invocation = side.Invocations.Single();
            Assert.AreEqual("unavailable", invocation.Inputs.Single(member => member.Name == "argument 0").Value.Kind);
            Assert.AreEqual("unavailable", invocation.Outputs.Single(member => member.Name == "argument 0").Value.Kind);
        }

        session.AddLine("call Scenario");
        Assert.AreEqual(4, session.Run().Value);
    }

    /// <summary>
    /// An imported span return reaches the scenario unchanged while the observation wrapper reports it as unavailable.
    /// </summary>
    /// <param name="name">The framework span type.</param>
    /// <returns>The completed span return and scenario assertions.</returns>
    [TestMethod]
    [DataRow("Span")]
    [DataRow("ReadOnlySpan")]
    public async Task Run_ImportedSpanReturn_LeavesTheValueAvailableToTheScenario(string name)
    {
        var span = "valuetype " + name + "<int32>";
        var session = IlLines.Load((".method " + span + " Read() {\nldc.i4.3\nnewarr int32\nnewobj instance void "
            + span + "::.ctor(int32[])\nret\n}").Split('\n'));
        var edit = session.PrepareEdit("Read", "Copy");
        session.CommitEdit(edit.Name, edit.Source.Replace("ldc.i4.3", "ldc.i4.4", StringComparison.Ordinal));
        foreach (var line in (".method int32 Scenario() {\n.locals init (" + span + " value)\ncall Copy\nstloc.0\n"
            + "ldloca 0\ncall instance int32 " + span + "::get_Length()\nret\n}").Split('\n'))
        {
            session.AddLine(line);
        }

        var result = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy using Scenario"),
            TestContext.CancellationToken);

        Assert.AreEqual("incomplete", result.Outcome);
        foreach (var side in new[] { result.Original, result.Edited })
        {
            Assert.AreEqual("completed", side.Outcome, side.Detail);
            Assert.IsNull(side.Exception);
            Assert.AreEqual(side == result.Original ? "3" : "4", side.Result!.Value);
            Assert.AreEqual("unavailable", side.Invocations.Single().Outputs.Single(member => member.Name == "return").Value.Kind);
        }
    }

    /// <summary>
    /// A custom ref struct remains unboxed through concrete and generic signatures that allow byref-like arguments.
    /// </summary>
    /// <param name="generic">Whether the selected method uses a generic parameter that permits ref structs.</param>
    /// <returns>The completed external type, generic constraint, and worker assertions.</returns>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Run_CustomRefStruct_UsesRuntimeTraits(bool generic)
    {
        var session = new Session();
        var (assembly, _, _) = CecilFixture.Build((module, owner) =>
        {
            var value = new TypeDefinition("N", "ComparisonRefValue", TypeAttributes.Public | TypeAttributes.Sealed
                | TypeAttributes.SequentialLayout, module.ImportReference(typeof(ValueType)));
            value.CustomAttributes.Add(new CustomAttribute(module.ImportReference(
                typeof(IsByRefLikeAttribute).GetConstructor(Type.EmptyTypes)!)));
            value.Fields.Add(new FieldDefinition("Value", FieldAttributes.Public, module.TypeSystem.Int32));
            module.Types.Add(value);
            var read = new MethodDefinition("Read", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Int32);
            owner.Methods.Add(read);
            TypeReference argument = value;
            if (generic)
            {
                var parameter = new GenericParameter("T", read)
                {
                    Attributes = (GenericParameterAttributes)RuntimeGenericAttributes.AllowByRefLike,
                };

                read.GenericParameters.Add(parameter);
                argument = parameter;
            }

            read.Parameters.Add(new ParameterDefinition(argument));
            read.Body.GetILProcessor().Emit(OpCodes.Ldc_I4, 41);
            read.Body.GetILProcessor().Emit(OpCodes.Ret);
        }, session.Resolver);

        Assert.IsTrue(assembly.GetType("N.ComparisonRefValue")!.IsByRefLike);
        var valueType = "valuetype [" + assembly.GetName().Name + "]N.ComparisonRefValue";
        var reference = "int32 [" + assembly.GetName().Name + "]N.Fixture::Read"
            + (generic ? "<" + valueType + ">(!!0)" : "(" + valueType + ")");
        var edit = session.PrepareEdit(reference, "Copy");
        session.CommitEdit(edit.Name, edit.Source.Replace("ldc.i4 41", "ldc.i4 42", StringComparison.Ordinal));
        foreach (var line in (".method int32 Scenario() {\n.locals init (" + valueType + " value)\nldloc.0\ncall Copy\nret\n}")
            .Split('\n'))
        {
            session.AddLine(line);
        }

        var result = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy using Scenario"),
            TestContext.CancellationToken);

        Assert.AreEqual("incomplete", result.Outcome);
        foreach (var side in new[] { result.Original, result.Edited })
        {
            Assert.AreEqual("completed", side.Outcome, side.Detail);
            Assert.IsNull(side.Exception);
            Assert.AreEqual(side == result.Original ? "41" : "42", side.Result!.Value);
            Assert.AreEqual("unavailable", side.Invocations.Single().Inputs.Single(member => member.Name == "argument 0").Value.Kind);
        }
    }
}
