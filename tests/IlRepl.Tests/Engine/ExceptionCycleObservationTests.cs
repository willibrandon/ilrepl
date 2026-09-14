using System.Reflection;
using IlRepl.Engine;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Observing a real exception cycle terminates without invoking the runtime's exception dispatch machinery.
/// </summary>
[TestClass]
public sealed class ExceptionCycleObservationTests
{
    /// <summary>
    /// The original messages remain available and the repeated exception carries an explicit observation problem.
    /// </summary>
    [TestMethod]
    public void Exception_CyclicChainRetainsMessagesAndReportsProblem()
    {
        var leaf = new ArgumentException("leaf detail");
        var outer = new InvalidOperationException("outer detail", leaf);
        typeof(Exception).GetField("_innerException", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(leaf, outer);

        var observed = new StructuralObservation(new Dictionary<string, string>()).Exception(outer);
        Assert.AreEqual("outer detail", observed.Message);
        Assert.AreEqual("leaf detail", observed.Inner!.Message);
        Assert.AreEqual(observed.Type, observed.Inner.Inner!.Type);
        Assert.AreEqual("exception chain is cyclic or exceeds the observation depth limit", observed.Inner.Inner.Problem);
        Assert.IsNull(observed.Inner.Inner.Inner);
    }
}
