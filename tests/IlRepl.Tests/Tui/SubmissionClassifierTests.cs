using IlRepl.Protocol;
using IlRepl.Tui;

namespace IlRepl.Tests.Tui;

/// <summary>
/// Tests for <see cref="SubmissionClassifier"/>: the four things a reply can mean.
/// </summary>
[TestClass]
public sealed class SubmissionClassifierTests
{
    private static HandleReply Reply(bool succeeded, long generation) =>
        new(succeeded, false, [], SessionStatus.Initial with { Mark = SessionMark.Initial with { Generation = generation } });

    /// <summary>
    /// The generation and the success flag decide.
    /// </summary>
    [TestMethod]
    public void Classify_FourOutcomes()
    {
        var mark = SessionMark.Initial with { Generation = 5 };
        Assert.AreEqual(SubmissionOutcome.Accepted, SubmissionClassifier.Classify(mark, Reply(true, 5)));
        Assert.AreEqual(SubmissionOutcome.Refused, SubmissionClassifier.Classify(mark, Reply(false, 5)));
        Assert.AreEqual(SubmissionOutcome.Completed, SubmissionClassifier.Classify(mark, Reply(true, 6)));
        Assert.AreEqual(SubmissionOutcome.Failed, SubmissionClassifier.Classify(mark, Reply(false, 6)));
    }
}
