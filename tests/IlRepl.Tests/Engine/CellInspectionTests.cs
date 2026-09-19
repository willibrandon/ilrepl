using System.Reflection;
using IlRepl.Engine;
using IlRepl.Tests.Shared;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Verifies passive cell emission exposes the implementation without activating dependencies or argument values.
/// </summary>
[TestClass]
public sealed class CellInspectionTests
{
    /// <summary>
    /// Inspection leaves module initialization and argument materialization deferred until a later explicit cell execution.
    /// </summary>
    [TestMethod]
    public void CompileForInspection_DoesNotActivateModuleOrMaterializeArguments()
    {
        using var files = new SessionWorkspaceFixture();
        var session = new Session { DeferActivation = true };
        CompiledCell? compiled = null;
        try
        {
            session.Resolver.LoadImage(ModuleInitializerFixture.Create(true, files.MarkerPath));
            foreach (var line in IlLines.Expand(
                ".method int32 Read() { call int32 Owner::Read(); ret }",
                ".args (int32 value = 42)", "ldarg value", "pop", "call Read"))
            {
                session.AddLine(line);
            }

            Assert.IsFalse(File.Exists(files.MarkerPath));
            Assert.IsNull(Assert.ContainsSingle(session.Cell.Arguments).Value);

            compiled = CellCompiler.CompileForInspection(session);

            Assert.IsNotNull(compiled.Implementation);
            Assert.AreEqual("Run", compiled.Implementation.Name);
            Assert.HasCount(1, compiled.Implementation.GetParameters());
            Assert.IsEmpty(compiled.ArgumentValues);
            Assert.IsTrue(session.DeferActivation);
            Assert.IsNull(Assert.ContainsSingle(session.Cell.Arguments).Value);
            Assert.IsFalse(File.Exists(files.MarkerPath), "Compilation must not run the captured module initializer.");
            Assert.AreEqual(142, session.Run().Value);
            Assert.AreEqual("initialized\n", File.ReadAllText(files.MarkerPath));
        }
        finally
        {
            compiled?.Release();
            session.Reset();
            session.Resolver.Dispose();
        }
    }

    /// <summary>
    /// Vararg inspection selects the actual vararg body and identifies its separate standard-convention invocation wrapper.
    /// </summary>
    [TestMethod]
    public void CompileForInspection_VarargExposesBodyBeforeInvocationWrapper()
    {
        var session = IlLines.Load(".vararg", "arglist", "pop", "ldc.i4.s 42");
        var compiled = CellCompiler.CompileForInspection(session);
        try
        {
            Assert.IsNotNull(compiled.Implementation);
            Assert.AreNotEqual(compiled.EntryPoint, compiled.Implementation);
            Assert.AreEqual("Run", compiled.Implementation.Name);
            Assert.IsTrue(compiled.Implementation.CallingConvention.HasFlag(CallingConventions.VarArgs));
            Assert.IsFalse(compiled.EntryPoint.CallingConvention.HasFlag(CallingConventions.VarArgs));
            Assert.IsNotEmpty(compiled.Implementation.GetMethodBody()!.GetILAsByteArray()!);
            Assert.IsFalse(session.State.IsEmpty);
        }
        finally
        {
            compiled.Release();
            session.Reset();
            session.Resolver.Dispose();
        }
    }

    /// <summary>
    /// Cells preserving explicit metadata and protected regions expose their body rather than their Reflection.Emit wrapper.
    /// </summary>
    [TestMethod]
    public void CompileForInspection_ProtectedRegionExposesMetadataBody()
    {
        var session = IlLines.Load(".maxstack 8", ".try {", "nop", "} finally {", "nop", "}", "ldc.i4.s 42");
        var compiled = CellCompiler.CompileForInspection(session);
        try
        {
            Assert.IsNotNull(compiled.Implementation);
            Assert.AreNotEqual(compiled.EntryPoint.Module.Assembly, compiled.Implementation.Module.Assembly);
            Assert.IsFalse(compiled.Implementation.Module.Assembly.IsDynamic);
            var clause = Assert.ContainsSingle(compiled.Implementation.GetMethodBody()!.ExceptionHandlingClauses);
            Assert.AreEqual(ExceptionHandlingClauseOptions.Finally, clause.Flags);
            Assert.IsEmpty(compiled.EntryPoint.GetMethodBody()!.ExceptionHandlingClauses);
            Assert.IsFalse(session.State.IsEmpty);
        }
        finally
        {
            compiled.Release();
            session.Reset();
            session.Resolver.Dispose();
        }
    }
}
