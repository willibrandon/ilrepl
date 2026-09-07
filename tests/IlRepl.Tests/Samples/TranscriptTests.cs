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

        Assert.IsNull(core.Status.OpenMethod, "a transcript must close its methods: " + Path.GetFileName(path));
        Assert.IsNull(core.Status.OpenType, "a transcript must close its classes: " + Path.GetFileName(path));
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

    /// <summary>
    /// The methods transcript: recursion, a void helper, calli, and a delegate over a session method.
    /// </summary>
    [TestMethod]
    public void Transcripts_MethodsProduceExpectedValues()
    {
        var core = new ReplCore();
        foreach (var line in File.ReadAllLines(Path.Combine(RepoPaths.Transcripts, "methods.il")))
        {
            core.Handle(line);
        }

        var results = core.Transcript.Lines.Where(l => l.Kind == LineKind.Result).Select(l => l.PlainText).ToList();
        Assert.HasCount(4, results);
        Assert.Contains("55", results[0]);
        Assert.Contains("(void)", results[1]);
        Assert.Contains("6765", results[2]);
        Assert.Contains("610", results[3]);
        Assert.Contains(l => l.Kind == LineKind.Output && l.PlainText == "hello, methods", core.Transcript.Lines);
        Assert.AreEqual(2, core.Status.Methods);
        Assert.AreEqual(7, core.CellNumber, "two closes and four runs");
    }

    /// <summary>
    /// The types transcript: a struct shown by its fields, a static that persists, an interface
    /// dispatched through a class, and a generic class instantiated from a cell.
    /// </summary>
    [TestMethod]
    public void Transcripts_TypesProduceExpectedValues()
    {
        var core = new ReplCore();
        foreach (var line in File.ReadAllLines(Path.Combine(RepoPaths.Transcripts, "types.il")))
        {
            core.Handle(line);
        }

        var results = core.Transcript.Lines.Where(l => l.Kind == LineKind.Result).Select(l => l.PlainText).ToList();
        Assert.HasCount(5, results);
        Assert.Contains("Point { X = 3, Y = 4 } : Point", results[0]);
        Assert.Contains("11", results[1]);
        Assert.Contains("2", results[2]);
        Assert.Contains("49", results[3]);
        Assert.Contains("\"boxed\"", results[4]);
        Assert.AreEqual(4, core.Status.Types);
    }
}
