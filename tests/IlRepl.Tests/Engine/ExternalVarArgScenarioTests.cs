using System.Globalization;
using System.Runtime.CompilerServices;
using IlRepl.Engine;
using IlRepl.Host;
using Mono.Cecil;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Windows comparison workers pass optional scenario arguments to both the external original and its edited copy.
/// </summary>
[TestClass]
public sealed class ExternalVarArgScenarioTests
{
    /// <summary>
    /// Supplies cancellation for real comparison processes.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// The original's ArgIterator sees every optional argument and both sides record the supplied values.
    /// </summary>
    /// <param name="optionalCount">The number of optional arguments passed by the scenario.</param>
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public async Task Compare_ExternalVarargOriginalReceivesOptionalArguments(int optionalCount)
    {
        var session = new Session();
        session.Resolver.Load(typeof(IsLong).Assembly.FullName!);
        var (assembly, _, _) = CecilFixture.Build(ExternalVarArgFixture.Define, session.Resolver);
        var edit = session.PrepareEdit("vararg int32 [" + assembly.GetName().Name + "]N.Fixture::Read(int32)", "Copy");
        Assert.IsNotEmpty(edit.Baseline.Problems);
        Assert.Contains(problem => problem.Contains("Native", StringComparison.Ordinal), edit.Baseline.Problems);
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        session.CommitEdit(edit.Name, """
            .method public static vararg int32 Read(int32 first) {
              .locals init (valuetype ArgIterator arguments)
              arglist
              newobj instance void ArgIterator::.ctor(valuetype RuntimeArgumentHandle)
              stloc.0
              ldloca.s 0
              call instance int32 ArgIterator::GetRemainingCount()
              ldarg.0
              add
              ldc.i4.1
              add
              ret
            }
            """);
        var source = new List<string> { ".method int32 Scenario() {", "ldc.i4.s 41" };
        source.AddRange(Enumerable.Repeat("ldc.i4.7", optionalCount));
        var optional = string.Concat(Enumerable.Repeat(", int32 modopt(System.Runtime.CompilerServices.IsLong)", optionalCount));
        source.Add("call vararg int32 Copy(int32, ..." + optional + ")");
        source.Add("ret");
        source.Add("}");
        foreach (var line in source)
        {
            session.AddLine(line);
        }

        var package = ComparisonCapture.Create(session, "Copy using Scenario");
        using (var original = new MemoryStream(package.Original.Image))
        {
            using var module = ModuleDefinition.ReadModule(original);
            var calls = module.GetTypes().SelectMany(type => type.Methods).Where(method => method.HasBody)
                .SelectMany(method => method.Body.Instructions).Select(instruction => instruction.Operand).OfType<MethodReference>();
            Assert.Contains(method => method.Name == "Read" && method.DeclaringType.Scope.Name == assembly.GetName().Name
                && method.CallingConvention == MethodCallingConvention.VarArg && method.Parameters.Count == optionalCount + 1, calls);
        }

        var result = await ProcessComparisonRunner.RunAsync(package, TestContext.CancellationToken);
        Assert.AreEqual("different", result.Outcome, result.Original.Detail + "; " + result.Edited.Detail);
        Assert.AreEqual((41 + optionalCount).ToString(CultureInfo.InvariantCulture), result.Original.Result!.Value);
        Assert.AreEqual((42 + optionalCount).ToString(CultureInfo.InvariantCulture), result.Edited.Result!.Value);
        foreach (var side in new[] { result.Original, result.Edited })
        {
            Assert.AreEqual("completed", side.Outcome);
            var inputs = side.Invocations.Single().Inputs;
            for (var index = 0; index < optionalCount; index++)
            {
                Assert.AreEqual("7", inputs.Single(member => member.Name == "argument " + (index + 1)).Value.Value);
            }
        }

        var direct = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy (41)"),
            TestContext.CancellationToken);
        Assert.AreEqual("different", direct.Outcome, direct.Original.Detail + "; " + direct.Edited.Detail);
        Assert.AreEqual("41", direct.Original.Result!.Value);
        Assert.AreEqual("42", direct.Edited.Result!.Value);
    }
}
