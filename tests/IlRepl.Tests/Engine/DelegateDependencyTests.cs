using System.Reflection;
using System.Runtime.Loader;
using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Tests.Shared;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Copies retain runtime delegate methods and invoke copied private targets after independent exports.
/// </summary>
[TestClass]
public sealed class DelegateDependencyTests
{
    /// <summary>
    /// Supplies cancellation for real comparison processes.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Private delegate constructors and invocation methods retain their runtime implementations in every emitted image.
    /// </summary>
    /// <param name="generic">Whether the delegate has a generic result type.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Commit_RuntimeDelegateRemainsExecutable(bool generic)
    {
        var session = new Session();
        session.Resolver.LoadImage(DelegateMetadataFixture.Create(generic));
        var edit = session.PrepareEdit("int32 Owner::Read()", "Copy");
        Assert.IsEmpty(edit.Problems, string.Join('\n', edit.Problems));
        session.CommitEdit(edit.Name, edit.Source.Replace("ret", "ldc.i4.1\nadd\nret", StringComparison.Ordinal));
        Assert.AreEqual(42, edit.OriginalMethod.Invoke(null, null));
        Assert.AreEqual(43, edit.Method!.Invoke(null, null));
        AssertDelegate(edit.Method.DeclaringType!, generic);
        var comparison = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy ()"),
            TestContext.CancellationToken);
        Assert.AreEqual("different", comparison.Outcome, comparison.Original.Detail + "; " + comparison.Edited.Detail);
        Assert.AreEqual("42", comparison.Original.Result!.Value);
        Assert.AreEqual("43", comparison.Edited.Result!.Value);
        session.AddLine("call Copy");
        foreach (var image in new[] { AssemblyExporter.Write(session, "delegate-copy"), IlasmLocator.Assemble(session.ToIlAsm()) })
        {
            var context = new AssemblyLoadContext("delegate-export", isCollectible: true);
            try
            {
                var assembly = context.LoadFromStream(new MemoryStream(image));
                Assert.AreEqual(43, assembly.GetType("IlRepl.Cell")!.GetMethod("Run")!.Invoke(null, null));
                AssertDelegate(assembly.GetType(edit.Method.DeclaringType!.FullName!)!, generic);
            }
            finally
            {
                context.Unload();
            }
        }
    }

    private static void AssertDelegate(Type owner, bool generic)
    {
        var callback = (Delegate)owner.GetField("Last")!.GetValue(null)!;
        Assert.AreSame(owner.Assembly, callback.GetType().Assembly);
        Assert.AreSame(owner, callback.Method.DeclaringType);
        Assert.IsTrue(callback.Method.IsPrivate);
        Assert.AreEqual(42, callback.DynamicInvoke());
        Assert.AreEqual(generic, callback.GetType().IsConstructedGenericType);
        if (generic)
        {
            Assert.AreEqual(typeof(int), callback.GetType().GetGenericArguments().Single());
        }

        var methods = callback.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Cast<MethodBase>().Concat(callback.GetType().GetConstructors()).ToArray();
        Assert.AreSequenceEqual([".ctor", "BeginInvoke", "EndInvoke", "Invoke"], methods.Select(method => method.Name).Order());
        foreach (var method in methods)
        {
            Assert.AreEqual(MethodImplAttributes.Runtime, method.GetMethodImplementationFlags() & MethodImplAttributes.CodeTypeMask);
            Assert.IsNull(method.GetMethodBody());
        }
    }
}
