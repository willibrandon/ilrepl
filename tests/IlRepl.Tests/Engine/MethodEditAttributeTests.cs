using System.Reflection;
using System.Runtime.Loader;
using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Tests.Shared;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Copied custom attributes remain constructible after session type replacement and independent assembly export.
/// </summary>
[TestClass]
public sealed class MethodEditAttributeTests
{
    /// <summary>
    /// Supplies cancellation for actual comparison workers.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Attribute discovery copies constructors and typed values without executing attribute code during capture or export.
    /// </summary>
    /// <returns>The completed capture, execution, and attribute construction assertions.</returns>
    [TestMethod]
    public async Task Commit_AttributeDependencies_AreIndependentAfterExport()
    {
        var session = IlLines.Load(AttributeDependencyExamples.Source().Split('\n'));
        var originalAttribute = session.Types.Single(type => type.RuntimeType!.Name == "Tag").RuntimeType!;
        var edit = session.PrepareEdit("int32 Tagged::Read(int32)", "Copy");
        session.CommitEdit(edit.Name, edit.Source.Replace("ret", "ldc.i4.1\nadd\nret", StringComparison.Ordinal));
        Assert.Contains(dependency => dependency.Location.Contains(": attribute Tag", StringComparison.Ordinal)
            && dependency.Disposition == "copied", edit.Dependencies);
        foreach (var line in IlLines.Expand(".class public Marker { .field public int64 Added; }"))
        {
            session.AddLine(line);
        }

        session.CommitEdit(edit.Name, edit.Source);
        var attribute = edit.Method!.Module.Assembly.GetTypes().Single(type => type.GetField("Runs") is not null);
        Assert.AreEqual(0, originalAttribute.GetField("Runs")!.GetValue(null));
        Assert.AreEqual(0, attribute.GetField("Runs")!.GetValue(null));
        var comparison = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy (41)"),
            TestContext.CancellationToken);
        Assert.AreEqual("different", comparison.Outcome, comparison.Original.Detail + "; " + comparison.Edited.Detail);
        Assert.AreEqual("41", comparison.Original.Result!.Value);
        Assert.AreEqual("42", comparison.Edited.Result!.Value);
        session.AddLine("ldc.i4.s 41");
        session.AddLine("call Copy");
        var images = new[] { AssemblyExporter.Write(session, "attribute-types"), IlasmLocator.Assemble(session.ToIlAsm()) };
        Assert.AreEqual(0, attribute.GetField("Runs")!.GetValue(null));
        foreach (var image in images)
        {
            var context = new AssemblyLoadContext("attribute-export", isCollectible: true);
            try
            {
                var exported = context.LoadImage(image);
                Assert.AreEqual(42, exported.GetType("IlRepl.Cell")!.GetMethod("Run")!.Invoke(null, null));
                var owner = exported.GetType(edit.Method.DeclaringType!.FullName!)!;
                var method = owner.GetMethod("Read")!;
                var attributes = owner.GetCustomAttributes().Concat(owner.GetField("Value")!.GetCustomAttributes())
                    .Concat(method.GetCustomAttributes()).Concat(method.ReturnParameter.GetCustomAttributes())
                    .Concat(method.GetParameters().Single().GetCustomAttributes()).ToArray();
                Assert.HasCount(5, attributes);
                foreach (var instance in attributes)
                {
                    var kind = (Type)instance.GetType().GetField("Kind")!.GetValue(instance)!;
                    Assert.AreSame(exported, kind.Assembly);
                    Assert.IsNotNull(kind.GetField("Original"));
                    Assert.IsNull(kind.GetField("Added"));
                    Assert.AreEqual(kind.MakeArrayType(), instance.GetType().GetField("Boxed")!.GetValue(instance));
                    Assert.AreSequenceEqual(new[] { kind, kind.MakeArrayType() },
                        (Type[])instance.GetType().GetField("Types")!.GetValue(instance)!);
                    Assert.AreEqual("saved", instance.GetType().GetProperty("Name")!.GetValue(instance));
                }
            }
            finally
            {
                context.Unload();
            }
        }

        Assert.AreEqual(0, originalAttribute.GetField("Runs")!.GetValue(null));
    }
}
