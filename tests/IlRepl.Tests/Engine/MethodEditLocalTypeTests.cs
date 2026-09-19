using System.Runtime.Loader;
using IlRepl.Engine;
using IlRepl.Host;
using Mono.Cecil;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Exact local signatures retain copied dependencies through revisions, isolated comparisons, and independent exports.
/// </summary>
[TestClass]
public sealed class MethodEditLocalTypeTests
{
    /// <summary>
    /// Supplies cancellation for actual comparison workers.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// A type named only by an exact local signature stays pinned after its session declaration changes.
    /// </summary>
    /// <param name="shape">The local signature containing the captured type.</param>
    /// <param name="introduced">Whether the edited body first introduces the local.</param>
    /// <returns>The completed execution, signature, dependency, and export assertions.</returns>
    [TestMethod]
    [DataRow("modopt", false)]
    [DataRow("modreq", false)]
    [DataRow("return", false)]
    [DataRow("parameter", false)]
    [DataRow("nested", false)]
    [DataRow("modopt", true)]
    [DataRow("modreq", true)]
    [DataRow("return", true)]
    [DataRow("parameter", true)]
    [DataRow("nested", true)]
    public async Task Commit_ExactLocalType_RemainsPinned(string shape, bool introduced)
    {
        var signature = shape switch
        {
            "modopt" => "int32 modopt(Marker)",
            "modreq" => "int32 modreq(Marker)",
            "return" => "method class Marker *(int32)",
            "parameter" => "method void *(class Marker)",
            _ => "method void *(method int32 modopt(Marker) *())",
        };

        var locals = ".locals init (" + signature + " value)\n";
        string Source(int value, bool declare) => ".method public static int32 Read() cil managed {\n"
            + (declare ? locals : "") + "ldc.i4.s " + value + "\nret\n}";
        var session = IlLines.Load((".class public Marker {\n.field public int32 Original\n}\n" + Source(41, !introduced)).Split('\n'));
        var edit = session.PrepareEdit("Read", "Copy");
        session.CommitEdit(edit.Name, Source(42, true));
        Assert.Contains(dependency => dependency.Symbol == "Marker" && dependency.Disposition == "copied (distinct type identity)"
            && dependency.Location.EndsWith(": local value", StringComparison.Ordinal), edit.Dependencies);
        foreach (var line in IlLines.Expand(".class public Marker { .field public int64 Added; }"))
        {
            session.AddLine(line);
        }

        if (!introduced)
        {
            session.CommitEdit(edit.Name, Source(43, true));
        }

        var expected = introduced ? 42 : 43;
        var captured = edit.Method!.Module.Assembly.GetTypes().Single(type => type.GetField("Original") is not null);
        Assert.IsNull(captured.GetField("Added"));
        Assert.IsNotNull(session.Types.Single().RuntimeType!.GetField("Added"));
        var comparison = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy ()"),
            TestContext.CancellationToken);
        Assert.AreEqual("different", comparison.Outcome, comparison.Original.Detail + "; " + comparison.Edited.Detail);
        Assert.AreEqual("41", comparison.Original.Result!.Value);
        Assert.AreEqual(expected.ToString(), comparison.Edited.Result!.Value);

        session.AddLine("call Copy");
        foreach (var image in new[] { AssemblyExporter.Write(session, "local-types"), IlasmLocator.Assemble(session.ToIlAsm()) })
        {
            using var module = ModuleDefinition.ReadModule(new MemoryStream(image));
            var local = module.GetTypes().Single(type => type.FullName == edit.Method.DeclaringType!.FullName)
                .Methods.Single(method => method.Name == "Read").Body.Variables.Single().VariableType;
            var referenced = shape switch
            {
                "modopt" or "modreq" => ((IModifierType)local).ModifierType,
                "return" => ((FunctionPointerType)local).ReturnType,
                "parameter" => ((FunctionPointerType)local).Parameters.Single().ParameterType,
                _ => ((IModifierType)((FunctionPointerType)((FunctionPointerType)local).Parameters.Single().ParameterType)
                    .ReturnType).ModifierType,
            };

            Assert.AreSame(module, referenced.Scope);
            Assert.AreEqual(captured.FullName, referenced.FullName);
            var context = new AssemblyLoadContext("local-types-export", isCollectible: true);
            try
            {
                var exported = context.LoadFromStream(new MemoryStream(image));
                Assert.AreEqual(expected, exported.GetType("IlRepl.Cell")!.GetMethod("Run")!.Invoke(null, null));
            }
            finally
            {
                context.Unload();
            }
        }
    }
}
