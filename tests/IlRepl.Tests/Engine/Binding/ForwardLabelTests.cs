using IlRepl.Engine;
using IlRepl.Engine.Binding;
using IlRepl.Protocol;
using IlRepl.Repl;

namespace IlRepl.Tests.Engine.Binding;

/// <summary>
/// Future labels follow the caret's body across declarations, execution boundaries and tolerant editing failures.
/// </summary>
[TestClass]
public sealed class ForwardLabelTests
{
    private static readonly CaretClassifier Classifier = new(new CilTokenizer(CilVocabularyBuilder.Vocabulary));

    /// <summary>
    /// The test runner's cancellation and reporting context.
    /// </summary>
    public required TestContext TestContext { get; set; }

    /// <summary>
    /// A declaration suspends the cell's label space and contributes no labels from its own body.
    /// </summary>
    [TestMethod]
    public void Declaration_ResumesTheCellLabelSpace()
    {
        string[] lines = ["br E", ".method void Helper() {", "HIDDEN: ret", "}", "END: ldc.i4.1", "ret"];
        using var editing = new EditingSession(new Session());
        var labels = editing.ForwardLabels(lines, 0, Classifier.Classify(lines[0], lines[0].Length, false),
            TestContext.CancellationToken);
        Assert.AreSequenceEqual(["END"], labels);
        var view = editing.Speculate(lines, 0, cancellationToken: TestContext.CancellationToken);
        Assert.IsEmpty(view.PendingLabels);
        Assert.IsEmpty(view.Stack);
    }

    /// <summary>
    /// Clear and reset start another label space that cannot supply a target to the earlier cell.
    /// </summary>
    [TestMethod]
    [DataRow(".clear")]
    [DataRow(".reset")]
    public void DestructiveBoundary_ExcludesLaterLabels(string boundary)
    {
        string[] lines = ["br E", boundary, "END: ldc.i4.1"];
        using var editing = new EditingSession(new Session());
        Assert.IsEmpty(editing.ForwardLabels(lines, 0, Classifier.Classify(lines[0], lines[0].Length, false),
            TestContext.CancellationToken));
    }

    /// <summary>
    /// A pending reference keeps top-level ret inside the cell until its target has been defined.
    /// </summary>
    [TestMethod]
    public void PendingTarget_KeepsRetInsideTheCell()
    {
        string[] lines = ["br E", "ret", "END: ldc.i4.1"];
        using var editing = new EditingSession(new Session());
        Assert.AreSequenceEqual(["END"], editing.ForwardLabels(
            lines, 0, Classifier.Classify(lines[0], lines[0].Length, false), TestContext.CancellationToken));
    }

    /// <summary>
    /// An unfinished switch operand still sees labels in its own body.
    /// </summary>
    [TestMethod]
    public void UnfinishedSwitch_SeedsTheCurrentReference()
    {
        string[] lines = ["ldc.i4.0", "switch (E", "ret", "END: ldc.i4.1"];
        using var editing = new EditingSession(new Session());
        Assert.AreSequenceEqual(["END"], editing.ForwardLabels(
            lines, 1, Classifier.Classify(lines[1], lines[1].Length, false), TestContext.CancellationToken));
    }

    /// <summary>
    /// A refused future instruction cannot hide a target by leaving its block comment open.
    /// </summary>
    [TestMethod]
    public void RefusedCommentLine_DoesNotHideTheTarget()
    {
        string[] lines = ["br E", "lcd.i4 3 /*", "END: ldc.i4.1"];
        using var editing = new EditingSession(new Session());
        Assert.AreSequenceEqual(["END"], editing.ForwardLabels(
            lines, 0, Classifier.Classify(lines[0], lines[0].Length, false), TestContext.CancellationToken));
    }
}
