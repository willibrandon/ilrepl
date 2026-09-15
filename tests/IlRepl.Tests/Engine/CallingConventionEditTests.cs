using System.Reflection;
using System.Runtime.Loader;
using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Tests.Shared;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Edited method headers control emitted calling conventions and the optional arguments delivered at runtime.
/// </summary>
[TestClass]
public sealed class CallingConventionEditTests
{
    /// <summary>
    /// Supplies cancellation for real comparison processes.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Both convention changes are visible in real PE metadata even on platforms that cannot execute managed varargs.
    /// </summary>
    /// <param name="originalVararg">Whether the original accepts optional arguments.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Export_EditedCallingConventionMatchesTheHeader(bool originalVararg)
    {
        var session = new Session();
        var assembly = session.Resolver.LoadImage(CallingConventionExamples.Create(originalVararg));
        var original = assembly.GetType("Owner")!.GetMethod("Read")!;
        var family = ImportedMethodFamily.Capture("Copy", original, session).Revise(CallingConventionExamples.Method(!originalVararg));
        var writer = new CecilWriter("convention-metadata");
        var definitions = family.Write(writer);
        var selected = (MethodDefinition)definitions[family.Selected.Method];
        var image = writer.Write();
        using var module = ModuleDefinition.ReadModule(new MemoryStream(image));
        var saved = (MethodDefinition)module.LookupToken(selected.MetadataToken);
        Assert.AreEqual(originalVararg ? MethodCallingConvention.Default : MethodCallingConvention.VarArg, saved.CallingConvention);
        Assert.IsFalse(saved.HasThis);
        Assert.IsFalse(saved.ExplicitThis);
        Assert.AreEqual(!originalVararg, saved.Body.Instructions.Any(instruction => instruction.OpCode == OpCodes.Arglist));
    }

    /// <summary>
    /// Committed copies use their edited convention in aliases, comparisons, and both executable export formats.
    /// </summary>
    /// <param name="originalVararg">Whether the original accepts optional arguments.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Edit_CallingConventionChangesReachRuntimeAndExports(bool originalVararg)
    {
        var session = new Session();
        var assembly = session.Resolver.LoadImage(CallingConventionExamples.Create(originalVararg));
        var reference = (originalVararg ? "vararg " : "") + "int32 [" + assembly.GetName().Name + "]Owner::Read()";
        var edit = session.PrepareEdit(reference, "Copy");
        if (!originalVararg)
        {
            session.CommitEdit(edit.Name, edit.Source);
            if (!OperatingSystem.IsWindows())
            {
                var previous = edit.Method;
                Assert.ThrowsExactly<ReplException>(() => session.CommitEdit(edit.Name, CallingConventionExamples.Method(true)));
                Assert.AreSame(previous, edit.Method);
                Assert.AreEqual(1, edit.Revision);
                Assert.AreEqual(42, edit.Method!.Invoke(null, null));
                return;
            }
        }

        session.CommitEdit(edit.Name, CallingConventionExamples.Method(!originalVararg));
        Assert.AreEqual(!originalVararg, edit.Method!.CallingConvention.HasFlag(CallingConventions.VarArgs));
        var call = originalVararg ? "call Copy" : "ldc.i4.7\ncall vararg int32 Copy(..., int32)";
        var expected = originalVararg ? 42 : 43;
        foreach (var line in call.Split('\n')) session.AddLine(line);
        Assert.AreEqual(expected, session.Run().Value);
        foreach (var line in call.Split('\n')) session.AddLine(line);
        foreach (var image in new[] { AssemblyExporter.Write(session, "convention-edit"), IlasmLocator.Assemble(session.ToIlAsm()) })
        {
            using var module = ModuleDefinition.ReadModule(new MemoryStream(image));
            var selected = module.GetType("IlRepl.Edits.Copy.Owner").Methods.Single(method => method.Name == "Read");
            Assert.AreEqual(originalVararg ? MethodCallingConvention.Default : MethodCallingConvention.VarArg, selected.CallingConvention);
            var context = new AssemblyLoadContext("convention-edit", isCollectible: true);
            try
            {
                var saved = context.LoadFromStream(new MemoryStream(image));
                Assert.AreEqual(expected, saved.GetType("IlRepl.Cell")!.GetMethod("Run")!.Invoke(null, null));
            }
            finally
            {
                context.Unload();
            }
        }

        if (OperatingSystem.IsWindows())
        {
            var result = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy ()"),
                TestContext.CancellationToken);
            Assert.AreEqual("match", result.Outcome, result.Original.Detail + "; " + result.Edited.Detail);
            Assert.AreEqual("42", result.Original.Result!.Value);
            Assert.AreEqual("42", result.Edited.Result!.Value);
        }
    }
}
