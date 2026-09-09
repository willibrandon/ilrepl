using IlRepl.Engine;
using IlRepl.Protocol;
using IlRepl.Repl;

namespace IlRepl.Tests.Repl;

/// <summary>
/// Array construction offers valid element types while preserving void tokens and pointer elements.
/// </summary>
[TestClass]
public sealed class ArrayElementCompletionTests
{
    /// <summary>
    /// Supplies cancellation for completion requests.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Invalid element shapes cannot be inserted by a confirmed newarr completion.
    /// </summary>
    /// <param name="prefix">The partial type name.</param>
    /// <param name="suffix">The retained compound-type suffix.</param>
    /// <param name="excluded">The type that must not appear among otherwise valid fuzzy matches.</param>
    [TestMethod]
    [DataRow("vo", "", "void")]
    [DataRow("System.Voi", "", "void")]
    [DataRow("vo", "[]", "void")]
    [DataRow("vo", "&", "void")]
    [DataRow("System.Spa", "<int32>", "Span")]
    [DataRow("typedre", "", "typedref")]
    public async Task Complete_Newarr_ExcludesInvalidElements(string prefix, string suffix, string excluded)
    {
        using var completer = new OperandCompleter(new Session());
        var before = "newarr " + prefix;
        var line = before + suffix;
        var reply = await completer.CompleteAsync(new CompletionRequest([line], 0, before.Length, null, []),
            TestContext.CancellationToken);
        Assert.DoesNotContain(item => item.InsertText.Split('<', '`')[0].Split('.').Last().Split(' ').Last() == excluded, reply.Items,
            string.Join(", ", reply.Items.Select(item => item.InsertText)));
    }

    /// <summary>
    /// Completing void remains executable as a type token and as an unmanaged pointer's target.
    /// </summary>
    /// <param name="pointerArray">Whether the completed type is an array element pointer.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Complete_VoidTokenAndPointerArray_BindsAndRuns(bool pointerArray)
    {
        var session = new Session();
        using var completer = new OperandCompleter(session);
        var before = pointerArray ? "newarr vo" : "ldtoken vo";
        var line = before + (pointerArray ? "*" : "");
        var reply = await completer.CompleteAsync(new CompletionRequest([line], 0, before.Length, null, []),
            TestContext.CancellationToken);
        var item = reply.Items.Single(item => item.InsertText == "void");
        if (pointerArray)
        {
            session.AddLine("ldc.i4.0");
        }

        session.AddLine(line[..reply.ReplaceStart] + item.InsertText + line[(reply.ReplaceStart + reply.ReplaceLength)..]);
        if (pointerArray)
        {
            var array = (Array)session.Run().Value!;
            Assert.AreEqual(typeof(void).MakePointerType(), array.GetType().GetElementType());
            Assert.HasCount(0, array);
        }
        else
        {
            session.AddLine("call Type::GetTypeFromHandle(RuntimeTypeHandle)");
            Assert.AreEqual(typeof(void), session.Run().Value);
        }
    }
}
