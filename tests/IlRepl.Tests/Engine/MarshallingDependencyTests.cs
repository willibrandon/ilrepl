using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Tests.Shared;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Marshal descriptor dependencies remain executable after edits and independent assembly exports.
/// </summary>
[TestClass]
public sealed class MarshallingDependencyTests
{
    /// <summary>
    /// Supplies cancellation for actual comparison workers.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// A descriptor-only session marshaler retains its factory and methods without running them during capture or export.
    /// </summary>
    /// <param name="target">The metadata target containing the native descriptor.</param>
    [TestMethod]
    [DataRow("field")]
    [DataRow("parameter")]
    [DataRow("return")]
    public async Task Commit_DescriptorDependencySurvivesExport(string target)
    {
        var session = IlLines.Load(MarshallingDependencyExamples.Source.Split('\n'));
        var originalMarshaler = session.Types.Single().RuntimeType!;
        var name = SessionAssemblies.NextName(SessionAssemblyKind.Types);
        var image = MarshallingMetadataFixture.Create(originalMarshaler.AssemblyQualifiedName!, target, name);
        Assert.IsTrue(SessionAssemblies.TryGetDefinition(originalMarshaler.Assembly, out var dependency));
        var definition = SessionAssemblies.Load(image, name, SessionAssemblyKind.Types, [dependency]);
        session.TypeTable.Add("Owner", definition.Assembly.GetType("Owner")!);
        var edit = session.PrepareEdit("int32 Owner::Read(int32)", "Copy");
        Assert.IsEmpty(edit.Problems, string.Join('\n', edit.Problems));
        session.CommitEdit(edit.Name, edit.Source.Replace("ret", "ldc.i4.1\nadd\nret", StringComparison.Ordinal));
        Assert.Contains(dependency => dependency.Location.Contains("marshalling", StringComparison.Ordinal)
            && dependency.Disposition.StartsWith("copied", StringComparison.Ordinal), edit.Dependencies);
        Assert.AreEqual(42, edit.OriginalMethod.Invoke(null, [42]));
        Assert.AreEqual(43, edit.Method!.Invoke(null, [42]));
        var copiedMarshaler = edit.Method.Module.Assembly.GetTypes().Single(type => typeof(ICustomMarshaler).IsAssignableFrom(type));
        Assert.AreEqual(0, originalMarshaler.GetField("Runs")!.GetValue(null));
        Assert.AreEqual(0, copiedMarshaler.GetField("Runs")!.GetValue(null));
        var comparison = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy (42)"),
            TestContext.CancellationToken);
        Assert.AreEqual("different", comparison.Outcome, comparison.Original.Detail + "; " + comparison.Edited.Detail);
        Assert.AreEqual("42", comparison.Original.Result!.Value);
        Assert.AreEqual("43", comparison.Edited.Result!.Value);
        session.AddLine("ldc.i4.s 42");
        session.AddLine("call Copy");
        var images = new[] { AssemblyExporter.Write(session, "marshalling-copy"), IlasmLocator.Assemble(session.ToIlAsm()) };
        Assert.AreEqual(0, copiedMarshaler.GetField("Runs")!.GetValue(null));
        foreach (var exportedImage in images)
        {
            var context = new AssemblyLoadContext("marshalling-export", isCollectible: true);
            try
            {
                var exported = context.LoadFromStream(new MemoryStream(exportedImage));
                Assert.AreEqual(43, exported.GetType("IlRepl.Cell")!.GetMethod("Run")!.Invoke(null, null));
                var owner = exported.GetType(edit.Method.DeclaringType!.FullName!)!;
                var method = owner.GetMethod("Read")!;
                var descriptor = target == "field" ? owner.GetField("Value")!.GetCustomAttribute<MarshalAsAttribute>()!
                    : (target == "return" ? method.ReturnParameter : method.GetParameters().Single())
                        .GetCustomAttribute<MarshalAsAttribute>()!;
                Assert.AreEqual(UnmanagedType.CustomMarshaler, descriptor.Value);
                Assert.AreEqual("saved cookie", descriptor.MarshalCookie);
                var marshaler = descriptor.MarshalTypeRef!;
                Assert.IsNotNull(marshaler);
                Assert.AreSame(exported, marshaler.Assembly);
                Assert.AreEqual(0, marshaler.GetField("Runs")!.GetValue(null));
                var instance = (ICustomMarshaler)marshaler.GetMethod("GetInstance")!.Invoke(null, [descriptor.MarshalCookie])!;
                Assert.AreEqual(1, marshaler.GetField("Runs")!.GetValue(null));
                Assert.AreEqual(42, instance.GetNativeDataSize());
                Assert.AreEqual((nint)42, instance.MarshalManagedToNative(42));
                Assert.AreEqual(43, instance.MarshalNativeToManaged(43));
                instance.CleanUpManagedData(42);
                instance.CleanUpNativeData(42);
            }
            finally
            {
                context.Unload();
            }
        }

        Assert.AreEqual(0, originalMarshaler.GetField("Runs")!.GetValue(null));
        var repeated = session.PrepareEdit("Copy", "Again");
        Assert.IsEmpty(repeated.Problems, string.Join('\n', repeated.Problems));
        session.CommitEdit(repeated.Name, repeated.Source);
        Assert.AreEqual(43, repeated.Method!.Invoke(null, [42]));
        var repeatedMarshaler = repeated.Method.Module.Assembly.GetTypes()
            .Single(type => typeof(ICustomMarshaler).IsAssignableFrom(type));
        var repeatedInstance = (ICustomMarshaler)repeatedMarshaler.GetMethod("GetInstance")!.Invoke(null, ["saved cookie"])!;
        Assert.AreEqual(42, repeatedInstance.GetNativeDataSize());
    }
}
