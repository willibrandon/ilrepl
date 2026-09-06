using IlRepl.Protocol;
using IlRepl.Repl;

namespace IlRepl.Tests.Samples;

/// <summary>
/// Runs every transcript in samples/Transcripts through the REPL and checks each produced a result without errors.
/// </summary>
[TestClass]
public sealed class TranscriptTests
{
    /// <summary>
    /// The transcript files, one test case each.
    /// </summary>
    public static IEnumerable<string> TranscriptFiles =>
        Directory.GetFiles(RepoPaths.Transcripts, "*.il").OrderBy(f => f, StringComparer.Ordinal);

    /// <summary>
    /// Every line succeeds and at least one result is printed.
    /// </summary>
    /// <param name="path">The transcript path.</param>
    [TestMethod]
    [DynamicData(nameof(TranscriptFiles))]
    public void Transcript_RunsWithoutErrors(string path)
    {
        var core = new ReplCore();
        var failures = new List<string>();
        foreach (var line in File.ReadAllLines(path))
        {
            var result = core.Handle(line);
            if (!result.Succeeded)
            {
                failures.Add(line + " => " + string.Join(" | ", core.Transcript.Lines.Where(l => l.Kind == LineKind.Error).Select(l => l.PlainText)));
            }
        }

        if (!core.Session.State.IsEmpty)
        {
            core.Handle("ret");
        }

        Assert.IsEmpty(failures, string.Join("\n", failures));
        Assert.Contains(l => l.Kind == LineKind.Result, core.Transcript.Lines, "no result line in " + Path.GetFileName(path));
    }

    /// <summary>
    /// Specific values from a few transcripts, so the files stay meaningful.
    /// </summary>
    [TestMethod]
    public void Transcripts_ProduceExpectedValues()
    {
        var core = new ReplCore();
        foreach (var line in File.ReadAllLines(Path.Combine(RepoPaths.Transcripts, "exceptions.il")))
        {
            core.Handle(line);
        }

        var results = core.Transcript.Lines.Where(l => l.Kind == LineKind.Result).Select(l => l.PlainText).ToList();
        Assert.HasCount(2, results);
        Assert.Contains("\"boom\"", results[0]);
        Assert.Contains("42", results[1]);
        Assert.Contains(l => l.Kind == LineKind.Output && l.PlainText == "finally ran", core.Transcript.Lines);
    }
}
