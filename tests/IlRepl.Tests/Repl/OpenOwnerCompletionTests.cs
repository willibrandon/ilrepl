using IlRepl.Engine;
using IlRepl.Protocol;
using IlRepl.Repl;

namespace IlRepl.Tests.Repl;

/// <summary>
/// Generic method completions on open framework constructions remain callable in real cells.
/// </summary>
[TestClass]
public sealed class OpenOwnerCompletionTests
{
    /// <summary>
    /// Supplies cancellation for completion requests.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// A generic framework method completes through its arguments and executes over a cell type parameter.
    /// </summary>
    /// <param name="nested">Whether the declaring argument contains the cell parameter inside another construction.</param>
    /// <param name="member">Whether the caller belongs to a generic session type.</param>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task Complete_GenericMethodOnOpenOwner_BindsAndRuns(bool nested, bool member)
    {
        var session = new Session();
        if (member)
        {
            session.AddLine(".class public GenericCaller<T> {");
            session.AddLine(".method public static int32 Check() {");
        }
        else
        {
            session.AddLine(".typeparams (T)");
            session.AddLine(".typeargs (object)");
        }

        var parameter = member ? "!0" : "!!0";
        var element = nested ? $"List<{parameter}>" : parameter;
        var owner = $"List<{element}>";
        session.AddLine($"newobj {owner}::.ctor()");
        session.AddLine("ldnull");
        session.AddLine("ldftn string Convert::ToString(object)");
        session.AddLine($"newobj Converter<{element}, string>::.ctor(object, native int)");
        using var completer = new OperandCompleter(session);
        var prefix = $"callvirt {owner}::Conv";
        var starter = await completer.CompleteAsync(new CompletionRequest([prefix], 0, prefix.Length, null, []),
            TestContext.CancellationToken);
        Assert.IsNotEmpty(starter.Items);
        var item = starter.Items.Single(item => item.InsertText.Contains("::ConvertAll<", StringComparison.Ordinal));
        Assert.IsTrue(item.Continues);
        var line = prefix[..starter.ReplaceStart] + item.InsertText + "string>";
        Assert.IsNotNull(item.Continuation);
        var anchor = new ContinuationAnchor(0, starter.ReplaceStart, starter.ReplaceStart + item.InsertText.Length, item.Continuation);
        var signature = await completer.CompleteAsync(new CompletionRequest([line], 0, line.Length, null, [anchor]),
            TestContext.CancellationToken);
        Assert.HasCount(1, signature.Items);
        line = line[..signature.ReplaceStart] + signature.Items[0].InsertText + line[(signature.ReplaceStart + signature.ReplaceLength)..];
        session.AddLine(line);
        session.AddLine("callvirt int32 List<string>::get_Count()");
        if (member)
        {
            session.AddLine("ret");
            session.AddLine("}");
            session.AddLine("}");
            session.AddLine("call GenericCaller<object>::Check()");
        }

        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".dll");
        try
        {
            session.Save(path);
            CheckExport(File.ReadAllBytes(path), !member);
            CheckExport(IlasmLocator.Assemble(session.ToIlAsm()), !member);
        }
        finally
        {
            File.Delete(path);
        }

        Assert.AreEqual(0, session.Run().Value);
    }

    /// <summary>
    /// An open owner's generic method definition keeps its arity when completed, emitted, and rendered.
    /// </summary>
    [TestMethod]
    public async Task Complete_GenericDefinitionToken_RetainsArity()
    {
        var session = new Session();
        session.AddLine(".typeparams (T)");
        session.AddLine(".typeargs (object)");
        using var completer = new OperandCompleter(session);
        const string prefix = "ldtoken method List<!!0>::ConvertAll";
        var reply = await completer.CompleteAsync(new CompletionRequest([prefix], 0, prefix.Length, null, []),
            TestContext.CancellationToken);
        var item = reply.Items.Single(item => !item.Continues);
        session.AddLine(prefix[..reply.ReplaceStart] + item.InsertText);
        session.AddLine("pop");
        session.AddLine("ldc.i4.0");
        CheckExport(IlasmLocator.Assemble(session.ToIlAsm()), true);
        Assert.AreEqual(0, session.Run().Value);
    }

    private static void CheckExport(byte[] image, bool generic)
    {
        var context = new System.Runtime.Loader.AssemblyLoadContext("open-owner-export", isCollectible: true);
        try
        {
            var assembly = context.LoadFromStream(new MemoryStream(image));
            var run = assembly.GetType("IlRepl.Cell")!.GetMethod("Run")!;
            if (generic)
            {
                run = run.MakeGenericMethod(typeof(object));
            }

            Assert.AreEqual(0, run.Invoke(null, null));
        }
        finally
        {
            context.Unload();
        }
    }
}
