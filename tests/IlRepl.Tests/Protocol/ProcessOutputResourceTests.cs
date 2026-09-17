using System.Threading.Channels;
using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Repl;
using IlRepl.Tests.Engine;

namespace IlRepl.Tests.Protocol;

/// <summary>
/// Verifies real worker output pipes are released at completion without relying on garbage collection.
/// </summary>
[TestClass]
public sealed class ProcessOutputResourceTests
{
    /// <summary>
    /// Supplies cancellation to isolated worker and filesystem synchronization.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Repeated native and behavioral comparisons close the exact output pipe handles observed in their live workers.
    /// </summary>
    /// <param name="native">Whether to inspect native code or compare behavioral observations.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [OSCondition(ConditionMode.Include, OperatingSystems.Linux)]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task Run_ClosesObservedWorkerPipesWithoutFinalization(bool native)
    {
        if (await IsolatedTestProcess.RunAsync(TestContext)) return;

        using var files = new SessionWorkspaceFixture();
        using var core = new ReplCore();
        foreach (var line in IlLines.Expand(
            ".method void Work(string prefix) {", ".locals init (string marker)",
            "ldarg.0", "call int32 Environment::get_ProcessId()", "box int32",
            "call string String::Concat(object, object)", "stloc.0", "ldloc.0",
            "ldstr \"/proc/self/fd/1\"", "newobj instance void System.IO.FileInfo::.ctor(string)",
            "callvirt instance string System.IO.FileSystemInfo::get_LinkTarget()", "ldstr \"\\n\"",
            "ldstr \"/proc/self/fd/2\"", "newobj instance void System.IO.FileInfo::.ctor(string)",
            "callvirt instance string System.IO.FileSystemInfo::get_LinkTarget()",
            "call string String::Concat(string, string, string)", "call void File::WriteAllText(string, string)",
            "ldloc.0", "ldstr \".ready\"", "call string String::Concat(string, string)", "ldstr \"ready\"",
            "call void File::WriteAllText(string, string)",
            "WAIT: ldloc.0", "ldstr \".release\"", "call string String::Concat(string, string)",
            "call bool File::Exists(string)", "brfalse WAIT", "ldstr \"output\"", "call void Console::Write(string)",
            "call class System.IO.TextWriter Console::get_Error()", "ldstr \"error\"",
            "callvirt instance void System.IO.TextWriter::Write(string)", "ret", "}"))
            Assert.IsTrue(core.Handle(line).Succeeded, line + "\n" + string.Join('\n', core.Transcript.Lines.Select(row => row.PlainText)));

        var edit = core.Session.PrepareEdit("Work", "Copy");
        core.Session.CommitEdit("Copy", edit.Source);
        var argument = LiteralParser.Escape(Path.Combine(files.DirectoryPath, "worker-"));
        var request = core.Handle(".jit Work (" + argument + ")");
        Assert.IsTrue(request.Succeeded);
        Assert.IsNotNull(request.NativePackage);
        var comparison = ComparisonCapture.Create(core.Session, "Copy (" + argument + ")");
        var ready = Channel.CreateUnbounded<string>();
        using var watcher = new FileSystemWatcher(files.DirectoryPath, "*.ready");
        watcher.Created += (_, args) => ready.Writer.TryWrite(args.FullPath);
        watcher.EnableRaisingEvents = true;

        for (var iteration = 0; iteration < 3; iteration++)
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
            var running = RunAsync(cancellation.Token);
            var observedPipes = new HashSet<string>(StringComparer.Ordinal);
            var noCollection = false;
            try
            {
                for (var side = 0; side < (native ? 1 : 2); side++)
                {
                    var entered = ready.Reader.ReadAsync(TestContext.CancellationToken).AsTask();
                    if (await Task.WhenAny(entered, running) == running)
                    {
                        var early = await running;
                        Assert.Fail("The worker completed before publishing its output pipe identities: " + early.Detail);
                    }

                    var marker = (await entered)[..^".ready".Length];
                    var pipes = await File.ReadAllLinesAsync(marker, TestContext.CancellationToken);
                    Assert.HasCount(2, pipes);
                    if (OperatingSystem.IsLinux())
                    {
                        var workerId = Path.GetFileName(marker)["worker-".Length..];
                        var directory = new DirectoryInfo("/proc/" + workerId + "/cwd").LinkTarget;
                        Assert.IsNotNull(directory);
                        var root = Path.GetDirectoryName(directory);
                        Assert.IsNotNull(root);
                        Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                            File.GetUnixFileMode(root), "Expected an owner-only control directory: " + root + "; worker cwd: " + directory);
                    }

                    var held = PipeLinks();
                    foreach (var pipe in pipes)
                    {
                        Assert.StartsWith("pipe:[", pipe);
                        Assert.Contains(pipe, held, "The live worker's redirected pipe must belong to the supervising process.");
                        Assert.IsTrue(observedPipes.Add(pipe), "Each worker must own a distinct output pipe.");
                    }

                    if (!noCollection)
                    {
                        noCollection = GC.TryStartNoGCRegion(64 * 1024 * 1024, disallowFullBlockingGC: true);
                        Assert.IsTrue(noCollection, "The isolated test must prevent finalizers from hiding unclosed output streams.");
                    }

                    await File.WriteAllTextAsync(marker + ".release", "release", TestContext.CancellationToken);
                }

                var result = await running;
                Assert.AreEqual(native ? "complete" : "match", result.Outcome, result.Detail);
                Assert.AreEqual("output", result.Output);
                Assert.AreEqual("error", result.Error);
                if (!native)
                {
                    Assert.AreEqual("output", result.EditedOutput);
                    Assert.AreEqual("error", result.EditedError);
                }

                var remaining = PipeLinks();
                foreach (var pipe in observedPipes)
                    Assert.DoesNotContain(pipe, remaining, $"Worker pipe {pipe} survived completion at iteration {iteration}.");
            }
            finally
            {
                try
                {
                    if (noCollection) GC.EndNoGCRegion();
                }
                finally
                {
                    await cancellation.CancelAsync();
                    await running;
                }
            }
        }

        async Task<(string Outcome, string? Detail, string Output, string Error, string? EditedOutput, string? EditedError)>
            RunAsync(CancellationToken cancellationToken)
        {
            if (native)
            {
                var reply = await ProcessNativeRunner.RunAsync(request.NativePackage, cancellationToken);
                return (reply.Outcome, reply.Left.Detail, reply.Left.StandardOutput, reply.Left.StandardError, null, null);
            }
            else
            {
                var reply = await ProcessComparisonRunner.RunAsync(comparison, cancellationToken);
                return (reply.Outcome, reply.Original.Detail + "\n" + reply.Edited.Detail,
                    reply.Original.StandardOutput, reply.Original.StandardError, reply.Edited.StandardOutput, reply.Edited.StandardError);
            }
        }
    }

    private static HashSet<string> PipeLinks()
    {
        var pipes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles("/proc/self/fd"))
        {
            var target = new FileInfo(path).LinkTarget;
            if (target is not null && target.StartsWith("pipe:[", StringComparison.Ordinal)) pipes.Add(target);
        }

        return pipes;
    }
}
