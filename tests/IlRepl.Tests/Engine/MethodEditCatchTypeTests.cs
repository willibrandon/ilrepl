using System.Runtime.Loader;
using IlRepl.Engine;
using IlRepl.Host;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MethodAttributes = Mono.Cecil.MethodAttributes;
using TypeAttributes = Mono.Cecil.TypeAttributes;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Catch-only dependencies retain the exception identities raised by external helpers.
/// </summary>
[TestClass]
public sealed class MethodEditCatchTypeTests
{
    /// <summary>
    /// Supplies cancellation for actual comparison workers.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// A private catch type matches exceptions from retained external helpers or helpers copied into the edit.
    /// </summary>
    /// <param name="copyHelper">Whether the throwing helper requires copying because it has assembly visibility.</param>
    /// <param name="structured">Whether the edited source uses structured catch syntax instead of label ranges.</param>
    /// <returns>The completed invocation, comparison, and export assertions.</returns>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task Prepare_CatchOnlyPrivateType_PreservesExternalExceptions(bool copyHelper, bool structured)
    {
        var session = new Session();
        var (assembly, dependencyImage, original) = CecilFixture.Build((module, owner) =>
        {
            var exception = new TypeDefinition("N", "HiddenException", TypeAttributes.NotPublic,
                module.ImportReference(typeof(Exception)));
            module.Types.Add(exception);
            var constructor = new MethodDefinition(".ctor", MethodAttributes.Public | MethodAttributes.SpecialName
                | MethodAttributes.RTSpecialName, module.TypeSystem.Void);
            exception.Methods.Add(constructor);
            var initialize = constructor.Body.GetILProcessor();
            initialize.Emit(OpCodes.Ldarg_0);
            initialize.Emit(OpCodes.Call, module.ImportReference(typeof(Exception).GetConstructor(Type.EmptyTypes)!));
            initialize.Emit(OpCodes.Ret);
            var helper = new TypeDefinition("N", "Thrower", TypeAttributes.Public, module.TypeSystem.Object);
            module.Types.Add(helper);
            var throwing = new MethodDefinition("Throw", MethodAttributes.Static
                | (copyHelper ? MethodAttributes.Assembly : MethodAttributes.Public), module.TypeSystem.Void);
            helper.Methods.Add(throwing);
            throwing.Body.GetILProcessor().Emit(OpCodes.Newobj, constructor);
            throwing.Body.GetILProcessor().Emit(OpCodes.Throw);
            var read = new MethodDefinition("Read", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Int32);
            owner.Methods.Add(read);
            read.Body.InitLocals = true;
            read.Body.Variables.Add(new VariableDefinition(module.TypeSystem.Int32));
            var il = read.Body.GetILProcessor();
            var start = il.Create(OpCodes.Call, throwing);
            var handler = il.Create(OpCodes.Pop);
            var end = il.Create(OpCodes.Ldloc_0);
            il.Append(start);
            il.Emit(OpCodes.Leave, end);
            il.Append(handler);
            il.Emit(OpCodes.Ldc_I4, 41);
            il.Emit(OpCodes.Stloc_0);
            il.Emit(OpCodes.Leave, end);
            il.Append(end);
            il.Emit(OpCodes.Ret);
            read.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
            {
                TryStart = start,
                TryEnd = handler,
                HandlerStart = handler,
                HandlerEnd = end,
                CatchType = exception,
            });
        }, session.Resolver);

        Assert.AreEqual(41, original.GetMethod("Read")!.Invoke(null, null));
        var edit = session.PrepareEdit("int32 [" + assembly.GetName().Name + "]N.Fixture::Read()", "Copy");
        Assert.IsEmpty(edit.Problems, string.Join("\n", edit.Problems));
        Assert.AreEqual(41, edit.OriginalMethod.Invoke(null, null));
        var qualifier = "[" + assembly.GetName().Name + "]N.";
        var source = structured ? ".method public static int32 Read() cil managed {\n.locals init (int32 value)\n.try {\n"
            + "call void " + qualifier + "Thrower::Throw()\nleave DONE\n} catch " + qualifier + "HiddenException {\n"
            + "pop\nldc.i4.s 42\nstloc.0\nleave DONE\n}\nDONE: ldloc.0\nret\n}"
            : edit.Source.Replace("ldc.i4 41", "ldc.i4 42", StringComparison.Ordinal);
        session.CommitEdit(edit.Name, source);
        Assert.AreEqual(42, edit.Method!.Invoke(null, null));
        var dependency = edit.Dependencies.Single(dependency => dependency.Symbol == "HiddenException");
        Assert.AreEqual(copyHelper ? "copied (distinct type identity)" : "external", dependency.Disposition);
        Assert.AreEqual("nonpublic", dependency.Access);
        Assert.EndsWith(": catch HiddenException", dependency.Location);
        var caught = edit.Method.GetMethodBody()!.ExceptionHandlingClauses.Single().CatchType!;
        Assert.AreEqual(copyHelper ? edit.Method.Module : original.Module, caught.Module);

        var comparison = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy ()"),
            TestContext.CancellationToken);
        Assert.AreEqual("different", comparison.Outcome, comparison.Original.Detail + "; " + comparison.Edited.Detail);
        Assert.AreEqual("41", comparison.Original.Result!.Value);
        Assert.AreEqual("42", comparison.Edited.Result!.Value);
        Assert.HasCount(1, comparison.Original.Invocations);
        Assert.HasCount(1, comparison.Edited.Invocations);

        session.AddLine("call Copy");
        foreach (var image in new[] { AssemblyExporter.Write(session, "catch-types"), IlasmLocator.Assemble(session.ToIlAsm()) })
        {
            var context = new AssemblyLoadContext("catch-types-export", isCollectible: true);
            try
            {
                context.LoadImage(dependencyImage);
                var exported = context.LoadImage(image);
                Assert.AreEqual(42, exported.GetType("IlRepl.Cell")!.GetMethod("Run")!.Invoke(null, null));
            }
            finally
            {
                context.Unload();
            }
        }
    }

    /// <summary>
    /// A session type used only by a catch clause is copied and stays pinned when its source declaration changes.
    /// </summary>
    /// <returns>The completed pinned catch-type and worker assertions.</returns>
    [TestMethod]
    public async Task Prepare_CatchOnlySessionType_PreservesCapturedIdentity()
    {
        var session = IlLines.Load(".class public LocalException extends Exception {", "}",
            ".method int32 Read() {", ".locals init (int32 value)", ".try {",
            "newobj instance void InvalidOperationException::.ctor()", "throw",
            "} catch LocalException {", "pop", "ldc.i4.s 41", "stloc.0", "leave DONE",
            "} catch Exception {", "pop", "ldc.i4.s 41", "stloc.0", "leave DONE", "}", "DONE: ldloc.0", "ret", "}");
        var edit = session.PrepareEdit("Read", "Copy");
        Assert.AreEqual("copied (distinct type identity)", edit.Dependencies.Single(dependency =>
            dependency.Symbol == "LocalException").Disposition);
        foreach (var line in new[] { ".class public LocalException extends Exception {", ".field public int64 Added", "}" })
        {
            session.AddLine(line);
        }

        session.CommitEdit(edit.Name, edit.Source.Replace("ldc.i4.s 41", "ldc.i4.s 42", StringComparison.Ordinal));
        var caught = edit.Method!.GetMethodBody()!.ExceptionHandlingClauses.Select(clause => clause.CatchType!)
            .Single(type => type.Module == edit.Method.Module);
        Assert.IsNull(caught.GetField("Added"));
        Assert.IsNotNull(session.Types.Single().RuntimeType!.GetField("Added"));
        Assert.AreEqual(41, edit.OriginalMethod.Invoke(null, null));
        Assert.AreEqual(42, edit.Method.Invoke(null, null));

        var result = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy ()"),
            TestContext.CancellationToken);
        Assert.AreEqual("different", result.Outcome, result.Original.Detail + "; " + result.Edited.Detail);
        Assert.AreEqual("41", result.Original.Result!.Value);
        Assert.AreEqual("42", result.Edited.Result!.Value);
    }
}
