using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Protocol;
using IlRepl.Tests.Shared;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Explicit parameter defaults reach edited methods, aliases, reflection, workers, and independent exports.
/// </summary>
[TestClass]
public sealed class EditedParameterDefaultTests
{
    /// <summary>
    /// Supplies cancellation for actual comparison worker processes.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// An explicit default replaces or adds metadata without changing the pinned original or coupling Optional to HasDefault.
    /// </summary>
    /// <param name="originalDefault">The original constant, or null when none exists.</param>
    /// <param name="isPrivate">Whether the alias forwards to a private target.</param>
    /// <param name="optional">Whether the edited parameter retains the original Optional flag.</param>
    /// <param name="duplicate">Whether an earlier .param directive must be superseded.</param>
    [TestMethod]
    [DataRow(7, false, true, false)]
    [DataRow(7, true, true, false)]
    [DataRow(null, false, true, false)]
    [DataRow(null, true, true, false)]
    [DataRow(7, true, false, false)]
    [DataRow(null, false, false, false)]
    [DataRow(7, true, true, true)]
    public async Task Commit_ExplicitParameterDefaultsReachEveryExecutable(
        int? originalDefault,
        bool isPrivate,
        bool optional,
        bool duplicate)
    {
        var session = IlLines.Load(ParameterDefaultComparisonExamples.Source(originalDefault, isPrivate, optional: true).Split('\n'));
        var edit = session.PrepareEdit("int32 Owner::Read(int32)", "Copy");
        Assert.IsEmpty(edit.Problems);
        session.CommitEdit(edit.Name, ParameterDefaultComparisonExamples.Method(8, isPrivate, optional, duplicate));
        Assert.IsNotNull(edit.Method);
        var alias = session.TypeTable.MethodAliases[edit.Name];
        foreach (var method in new[] { edit.Original.Requested, edit.OriginalMethod })
        {
            AssertDefault(method, originalDefault, optional: true);
            if (originalDefault is { } original)
            {
                Assert.AreEqual(original, method.Invoke(null, [Type.Missing]));
            }
            else
            {
                Assert.ThrowsExactly<ArgumentException>(() => method.Invoke(null, [Type.Missing]));
            }
        }

        foreach (var method in new[] { edit.Method, alias })
        {
            AssertDefault(method, 8, optional);
            Assert.AreEqual(8, method.Invoke(null, [Type.Missing]));
            Assert.AreEqual(42, method.Invoke(null, [42]));
        }

        Assert.AreEqual(isPrivate, edit.Method.IsPrivate);
        Assert.IsTrue(alias.IsPublic);
        Add(session, ParameterDefaultComparisonExamples.Scenarios());
        var metadata = await CompareAsync(session, "Scenario");
        Assert.AreEqual("different", metadata.Outcome, Details(metadata));
        AssertSide(metadata.Original, (originalDefault ?? -1).ToString(CultureInfo.InvariantCulture), "42");
        AssertSide(metadata.Edited, "8", "42");
        var missing = await CompareAsync(session, "MissingScenario");
        Assert.AreEqual("different-inputs", missing.Outcome, Details(missing));
        AssertSide(missing.Original, "7", "7");
        AssertSide(missing.Edited, "8", "8");
        AssertExports(session, edit, optional);
        AssertDefault(edit.Original.Requested, originalDefault, optional: true);
        AssertDefault(edit.OriginalMethod, originalDefault, optional: true);
    }

    /// <summary>
    /// An explicit null constant remains a real default on both a private target and its public callable alias.
    /// </summary>
    [TestMethod]
    public void Commit_NullParameterDefaultRemainsPresent()
    {
        var session = IlLines.Load(".class public Owner {", ".method private static string Read([opt] string value) {",
            ".param [1] = \"before\"", "ldarg.0", "ret", "}", "}");
        var edit = session.PrepareEdit("string Owner::Read(string)", "Copy");
        session.CommitEdit(edit.Name, ".method private static string Read([opt] string value) {\n"
            + ".param [1] = nullref\nldarg.0\nret\n}");
        Assert.AreEqual("before", edit.OriginalMethod.Invoke(null, [Type.Missing]));
        foreach (var method in new[] { edit.Method!, session.TypeTable.MethodAliases[edit.Name] })
        {
            var parameter = Assert.ContainsSingle(method.GetParameters());
            Assert.IsTrue(parameter.HasDefaultValue);
            Assert.IsNull(parameter.RawDefaultValue);
            Assert.AreEqual(ParameterAttributes.Optional | ParameterAttributes.HasDefault, parameter.Attributes);
            Assert.IsNull(method.Invoke(null, [Type.Missing]));
        }

        session.AddLine("ldnull");
        session.AddLine("call Copy");
        foreach (var image in Images(session))
        {
            var context = new AssemblyLoadContext("null-default", isCollectible: true);
            try
            {
                var assembly = context.LoadImage(image);
                foreach (var method in new[] { edit.Method!, session.TypeTable.MethodAliases[edit.Name] }
                    .Select(source => ExportedMethod(assembly, source)))
                {
                    var parameter = Assert.ContainsSingle(method.GetParameters());
                    Assert.IsTrue(parameter.HasDefaultValue);
                    Assert.IsNull(parameter.RawDefaultValue);
                    Assert.IsNull(method.Invoke(null, [Type.Missing]));
                }
            }
            finally
            {
                context.Unload();
            }
        }
    }

    /// <summary>
    /// A newly added optional parameter carries its explicit default beyond the original signature's parameter count.
    /// </summary>
    /// <param name="isPrivate">Whether the edited signature needs a public callable alias.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Commit_AddedParameterDefaultReachesTargetAliasAndExports(bool isPrivate)
    {
        var session = IlLines.Load(ParameterDefaultComparisonExamples.Source(null, isPrivate, optional: false).Split('\n'));
        var edit = session.PrepareEdit("int32 Owner::Read(int32)", "Copy");
        session.CommitEdit(edit.Name, ".method " + (isPrivate ? "private" : "public")
            + " static int32 Read(int32 value, [opt] int32 increment) {\n.param [2] = int32(8)\nldarg.0\nldarg.1\nadd\nret\n}");
        foreach (var method in new[] { edit.Original.Requested, edit.OriginalMethod })
        {
            Assert.HasCount(1, method.GetParameters());
            Assert.AreEqual(42, method.Invoke(null, [42]));
        }

        var alias = session.TypeTable.MethodAliases[edit.Name];
        foreach (var method in new[] { edit.Method!, alias })
        {
            AssertAddedDefault(method);
        }

        session.AddLine("ldc.i4.s 42");
        session.AddLine("ldc.i4.8");
        session.AddLine("call Copy");
        foreach (var image in Images(session))
        {
            var context = new AssemblyLoadContext("added-parameter-default", isCollectible: true);
            try
            {
                var assembly = context.LoadImage(image);
                foreach (var method in new[] { edit.Method!, alias })
                {
                    AssertAddedDefault(ExportedMethod(assembly, method));
                }

                Assert.AreEqual(50, assembly.GetType("IlRepl.Cell")!.GetMethod("Run")!.Invoke(null, null));
            }
            finally
            {
                context.Unload();
            }
        }
    }

    /// <summary>
    /// Invalid constant edits leave the previous executable, alias, source, and revisions intact before recovery.
    /// </summary>
    [TestMethod]
    public void Commit_InvalidParameterDefaultPreservesPublishedRevision()
    {
        var session = IlLines.Load(ParameterDefaultComparisonExamples.Source(7, isPrivate: true, optional: true).Split('\n'));
        var edit = session.PrepareEdit("int32 Owner::Read(int32)", "Copy");
        session.CommitEdit(edit.Name, edit.Source);
        var previous = edit.Method;
        var alias = session.TypeTable.MethodAliases[edit.Name];
        var source = edit.Source;
        var revision = session.CompletionRevision;
        var invalid = ParameterDefaultComparisonExamples.Method(8, isPrivate: true, optional: true)
            .Replace("int32(8)", "\"wrong type\"", StringComparison.Ordinal);
        var failure = Assert.ThrowsExactly<ReplException>(() => session.CommitEdit(edit.Name, invalid));
        Assert.Contains("parameter 1", failure.Message);
        Assert.Contains("int32", failure.Message);
        Assert.AreSame(previous, edit.Method);
        Assert.AreSame(alias, session.TypeTable.MethodAliases[edit.Name]);
        Assert.AreEqual(source, edit.Source);
        Assert.AreEqual(1, edit.Revision);
        Assert.AreEqual(revision, session.CompletionRevision);
        Assert.AreEqual(7, alias.Invoke(null, [Type.Missing]));
        session.CommitEdit(edit.Name, ParameterDefaultComparisonExamples.Method(8, isPrivate: true, optional: true));
        Assert.AreEqual(2, edit.Revision);
        Assert.AreEqual(8, edit.Method!.Invoke(null, [Type.Missing]));
        Assert.AreEqual(8, session.TypeTable.MethodAliases[edit.Name].Invoke(null, [Type.Missing]));
        Assert.AreEqual(7, edit.OriginalMethod.Invoke(null, [Type.Missing]));
    }

    private static void AssertDefault(MethodBase method, int? value, bool optional)
    {
        var parameter = Assert.ContainsSingle(method.GetParameters());
        Assert.AreEqual("value", parameter.Name);
        Assert.AreEqual(optional, parameter.IsOptional);
        Assert.AreEqual(value.HasValue, parameter.HasDefaultValue);
        var flags = (optional ? ParameterAttributes.Optional : ParameterAttributes.None)
            | (value.HasValue ? ParameterAttributes.HasDefault : ParameterAttributes.None);
        Assert.AreEqual(flags, parameter.Attributes);
        if (value is { } constant)
        {
            Assert.AreEqual(constant, parameter.RawDefaultValue);
        }
        else
        {
            Assert.AreSame(optional ? Missing.Value : DBNull.Value, parameter.RawDefaultValue);
        }
    }

    private static void AssertAddedDefault(MethodBase method)
    {
        var parameters = method.GetParameters();
        Assert.HasCount(2, parameters);
        Assert.IsFalse(parameters[0].IsOptional);
        Assert.IsFalse(parameters[0].HasDefaultValue);
        Assert.AreEqual("increment", parameters[1].Name);
        Assert.AreEqual(ParameterAttributes.Optional | ParameterAttributes.HasDefault, parameters[1].Attributes);
        Assert.IsTrue(parameters[1].HasDefaultValue);
        Assert.AreEqual(8, parameters[1].RawDefaultValue);
        Assert.AreEqual(50, method.Invoke(null, [42, Type.Missing]));
    }

    private static void AssertExports(Session session, MethodEdit edit, bool optional)
    {
        session.AddLine("call MissingScenario");
        var alias = session.TypeTable.MethodAliases[edit.Name];
        foreach (var image in Images(session))
        {
            var context = new AssemblyLoadContext("parameter-default", isCollectible: true);
            try
            {
                var assembly = context.LoadImage(image);
                foreach (var method in new[] { edit.Method!, alias }.Select(source => ExportedMethod(assembly, source)))
                {
                    AssertDefault(method, 8, optional);
                    Assert.AreEqual(8, method.Invoke(null, [Type.Missing]));
                }

                var cell = assembly.GetType("IlRepl.Cell")!;
                Assert.AreEqual(8, cell.GetMethod("Run")!.Invoke(null, null));
                Assert.AreEqual(8, cell.GetMethod("Scenario")!.Invoke(null, null));
            }
            finally
            {
                context.Unload();
            }
        }
    }

    private static IEnumerable<byte[]> Images(Session session)
    {
        yield return AssemblyExporter.Write(session, "parameter-default");
        yield return IlasmLocator.Assemble(session.ToIlAsm());
    }

    private static MethodInfo ExportedMethod(Assembly assembly, MethodBase source) =>
        assembly.GetType(source.DeclaringType!.FullName!)!.GetMethod(source.Name,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;

    private static void AssertSide(ComparisonSide side, string result, string argument)
    {
        Assert.AreEqual("completed", side.Outcome, side.Detail);
        Assert.IsNull(side.Exception);
        Assert.IsNotNull(side.Result);
        Assert.AreEqual("scalar", side.Result.Kind);
        Assert.AreEqual(result, side.Result.Value);
        var invocation = Assert.ContainsSingle(side.Invocations);
        Assert.IsNull(invocation.Exception);
        Assert.AreEqual(argument, invocation.Inputs.Single(member => member.Name == "argument 0").Value.Value);
        Assert.AreEqual(argument, invocation.Outputs.Single(member => member.Name == "argument 0").Value.Value);
        Assert.AreEqual(argument, invocation.Outputs.Single(member => member.Name == "return").Value.Value);
    }

    private static void Add(Session session, string source)
    {
        foreach (var line in source.Split('\n'))
        {
            session.AddLine(line);
        }
    }

    private Task<ComparisonReply> CompareAsync(Session session, string scenario) =>
        ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy using " + scenario), TestContext.CancellationToken);

    private static string Details(ComparisonReply result) => result.Original.Detail + "; " + result.Edited.Detail;
}
