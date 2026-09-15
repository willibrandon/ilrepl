using System.Reflection;
using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Tests.Shared;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Direct comparison validates both executable signatures before export and preserves valid direct and scenario execution.
/// </summary>
[TestClass]
public sealed class DirectComparisonEligibilityTests
{
    /// <summary>
    /// Supplies cancellation for the actual comparison processes.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Receiver changes reject atomically, instance direct calls reject before capture, and supported comparisons still execute.
    /// </summary>
    /// <param name="originalStatic">Whether the captured original is static.</param>
    /// <param name="editedStatic">Whether the proposed edited method is static.</param>
    /// <param name="generic">Whether the selected method is closed over int32.</param>
    /// <returns>The completed rejection and recovered worker assertions.</returns>
    [TestMethod]
    [DataRow(false, true, false)]
    [DataRow(true, false, false)]
    [DataRow(false, false, false)]
    [DataRow(false, true, true)]
    [DataRow(true, false, true)]
    [DataRow(false, false, true)]
    public async Task Create_ReceiverRequirementsRejectBeforeExecution(bool originalStatic, bool editedStatic, bool generic)
    {
        var session = IlLines.Load(DirectComparisonEligibilityExamples.Source(originalStatic, generic).Split('\n'));
        var edit = session.PrepareEdit(DirectComparisonEligibilityExamples.Reference(originalStatic, generic), "Copy");
        session.CommitEdit(edit.Name, DirectComparisonEligibilityExamples.Method(originalStatic, generic, true));
        Assert.AreEqual(originalStatic, edit.Original.Requested.IsStatic);
        Assert.AreEqual(originalStatic, edit.Method!.IsStatic);
        Assert.IsFalse(edit.Original.Requested.ContainsGenericParameters);
        Assert.IsFalse(edit.Method.ContainsGenericParameters);
        var revision = session.CompletionRevision;
        var generation = session.Generation;
        var source = edit.Source;
        var committed = edit.Method;
        if (originalStatic != editedStatic)
        {
            var rejected = Assert.ThrowsExactly<ReplException>(() => session.CommitEdit(edit.Name,
                DirectComparisonEligibilityExamples.Method(editedStatic, generic, true)));
            Assert.AreEqual("an edit must preserve the method name and instance/static calling convention", rejected.Message);
            Assert.AreSame(committed, edit.Method);
        }

        if (!originalStatic)
        {
            var error = Assert.ThrowsExactly<ReplException>(() => ComparisonCapture.Create(session, "Copy ()"));
            Assert.Contains("the original Copy is an instance method", error.Message);
            Assert.Contains("both versions to be static", error.Message);
            Assert.Contains("parameterless CIL scenario", error.Message);
            Assert.Contains("matching original and edited signatures", error.Message);
        }
        else
        {
            _ = ComparisonCapture.Create(session, "Copy ()");
        }

        Assert.AreEqual(revision, session.CompletionRevision);
        Assert.AreEqual(generation, session.Generation);
        Assert.AreEqual(1, edit.Revision);
        Assert.AreEqual(source, edit.Source);
        AssertUncalled(edit.Original.Requested, edit.OriginalMethod, edit.Method);
        Assert.AreEqual(41, Invoke(edit.Original.Requested));
        Assert.AreEqual(42, Invoke(edit.Method));

        if (!originalStatic)
        {
            Add(session, DirectComparisonEligibilityExamples.Scenario(false));
        }

        var result = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session,
            originalStatic ? "Copy ()" : "Copy using Scenario"), TestContext.CancellationToken);

        Assert.AreEqual("different", result.Outcome, result.Original.Detail + "; " + result.Edited.Detail);
        foreach (var side in new[] { result.Original, result.Edited })
        {
            Assert.AreEqual("completed", side.Outcome, side.Detail);
            Assert.HasCount(1, side.Invocations);
            Assert.IsNull(side.Exception);
        }

        Assert.AreEqual("41", result.Original.Result!.Value);
        Assert.AreEqual("42", result.Edited.Result!.Value);
    }

    /// <summary>
    /// Open parameters reject direct capture while a matching scenario supplies the generic arguments to both versions.
    /// </summary>
    /// <returns>The completed preflight and scenario assertions.</returns>
    [TestMethod]
    public async Task Create_OpenGenericParameters_RejectsBeforeExecution()
    {
        var session = IlLines.Load(DirectComparisonEligibilityExamples.Source(true, true).Split('\n'));
        var edit = session.PrepareEdit(DirectComparisonEligibilityExamples.Reference(true, true, false), "Copy");
        session.CommitEdit(edit.Name, DirectComparisonEligibilityExamples.Method(true, true, true));
        Assert.IsTrue(edit.Original.Requested.ContainsGenericParameters);
        Assert.IsTrue(edit.Method!.ContainsGenericParameters);
        var revision = session.CompletionRevision;

        var error = Assert.ThrowsExactly<ReplException>(() => ComparisonCapture.Create(session, "Copy ()"));

        Assert.Contains("the original Copy has unbound generic parameters", error.Message);
        Assert.Contains("both versions to be closed", error.Message);
        Assert.Contains("Select a closed generic method with .edit", error.Message);
        Assert.Contains("parameterless CIL scenario", error.Message);
        Assert.AreEqual(revision, session.CompletionRevision);
        Assert.AreEqual(1, edit.Revision);
        AssertUncalled(edit.Original.Requested, edit.OriginalMethod, edit.Method);
        Add(session, DirectComparisonEligibilityExamples.Scenario(true, true));

        var result = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy using Scenario"),
            TestContext.CancellationToken);

        Assert.AreEqual("different", result.Outcome, result.Original.Detail + "; " + result.Edited.Detail);
        Assert.AreEqual("completed", result.Original.Outcome, result.Original.Detail);
        Assert.AreEqual("completed", result.Edited.Outcome, result.Edited.Detail);
        Assert.AreEqual("41", result.Original.Result!.Value);
        Assert.AreEqual("42", result.Edited.Result!.Value);
        Assert.HasCount(1, result.Original.Invocations);
        Assert.HasCount(1, result.Edited.Invocations);
    }

    private static object? Invoke(MethodBase method) => method.Invoke(method.IsStatic ? null
        : Activator.CreateInstance(method.DeclaringType!), null);

    private static void AssertUncalled(params MethodBase[] methods)
    {
        foreach (var method in methods)
        {
            Assert.AreEqual(0, method.DeclaringType!.GetField("Runs")!.GetValue(null));
        }
    }

    private static void Add(Session session, string source)
    {
        foreach (var line in source.Split('\n'))
        {
            session.AddLine(line);
        }
    }
}
