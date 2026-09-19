using System.Globalization;
using System.Text;
using Hex1b;
using Hex1b.Automation;
using Hex1b.Input;
using IlRepl.Processes;
using IlRepl.Protocol;

namespace IlRepl.Tests.EndToEnd;

/// <summary>
/// Verifies real console input and terminal-mode restoration around bounded startup capability discovery.
/// </summary>
[TestClass]
[TestCategory("Interaction")]
public sealed class ConsoleStartupTests
{
    /// <summary>
    /// Supplies cancellation to child processes and terminal interaction.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Unicode input during capability probing or alternate-screen entry remains an exact inert draft and later executes.
    /// </summary>
    /// <param name="paste">Whether the early input is a bracketed multiline paste.</param>
    /// <param name="atTuiEntry">Whether input arrives at alternate-screen entry instead of the Unix capability probe.</param>
    [TestMethod]
    [DynamicData(nameof(EarlyInputModes))]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Probe_PreservesEarlyUnicodeInput(bool paste, bool atTuiEntry)
    {
        var token = TestContext.CancellationToken;
        using var files = new SessionWorkspaceFixture();
        var source = "ldc.i4.s 42 // early λ日本" + (paste ? "\nret" : "");
        var input = Encoding.UTF8.GetBytes(paste ? "\x1b[200~" + source + "\x1b[201~" : source);
        var filter = new StartupInputFilter(input, atTuiEntry);
        await using var terminal = Create(files, [RepoPaths.FrontEndAssembly, "--no-history"])
            .AddPresentationFilter(filter).Build();
        filter.Terminal = terminal;
        var run = terminal.RunAsync(token);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(20));
        await filter.Written.Task.WaitAsync(TimeSpan.FromSeconds(20), token);
        await auto.WaitUntilAsync(snapshot => snapshot.ContainsText("early λ日本")
            && (paste ? snapshot.ContainsText("stack [int32]") || snapshot.ContainsText("stack before [int32]")
                : snapshot.ContainsText("stack before []")));
        await auto.Ctrl().KeyAsync(Hex1bKey.S, ct: token);
        await auto.WaitUntilTextAsync("Save session");
        await auto.Ctrl().KeyAsync(Hex1bKey.A, ct: token);
        await auto.TypeAsync(files.SessionPath, ct: token);
        await auto.EnterAsync(ct: token);
        await auto.WaitUntilTextAsync("saved session ");
        var saved = SessionCodec.Read(await File.ReadAllBytesAsync(files.SessionPath, token));
        Assert.AreEqual(source, string.Join('\n', saved.Editor.Lines));
        Assert.IsEmpty(saved.Entries, "Early input must remain in the editor until explicitly submitted.");
        await auto.EnterAsync(ct: token);
        if (!paste)
        {
            await auto.WaitUntilTextAsync("stack [int32]");
            await auto.TypeAsync("ret", ct: token);
            await auto.EnterAsync(ct: token);
        }

        await auto.WaitUntilTextAsync("= 42 : int32");
        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: token);
        await auto.WaitUntilTextAsync("Save changes to ");
        await auto.DownAsync(ct: token);
        await auto.EnterAsync(ct: token);
        Assert.AreEqual(0, await run.WaitAsync(token));
    }

    /// <summary>
    /// Exercises alternate-screen entry everywhere and capability probing where the actual console driver performs it.
    /// </summary>
    public static IEnumerable<(bool Paste, bool AtTuiEntry)> EarlyInputModes()
    {
        for (var paste = 0; paste < 2; paste++)
        {
            if (!OperatingSystem.IsWindows())
            {
                yield return (paste != 0, false);
            }

            yield return (paste != 0, true);
        }
    }

    /// <summary>
    /// Complete and independently acknowledged fragments of late graphics and background responses never edit the draft.
    /// </summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Probe_LateProtocolRepliesDoNotBecomeInput()
    {
        var token = TestContext.CancellationToken;
        using var files = new SessionWorkspaceFixture();
        await using var terminal = Create(files,
            [typeof(ConsoleStartupProbe).Assembly.Location, "--console-startup-probe", "editor", files.DirectoryPath]).Build();
        var run = terminal.RunAsync(token);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(20));
        await auto.WaitUntilTextAsync("il[1]>");
        const string draft = "// retained λ日本";
        await auto.TypeAsync(draft, ct: token);
        await auto.WaitUntilTextAsync(draft);
        await SendObservedAsync("\x1b_Gi=991122;OK\x1b\\\x1b]11;rgb:1111/2222/3333\x1b\\");
        await SendObservedAsync("\x1b_Gi=991144;ERROR:" + new string('x', 600) + "\x1b\\", requireMultipleReads: true);
        await SendFragmentedAsync("\x1b_Gi=991133;", "OK\x1b\\");
        await SendFragmentedAsync("\x1b]11;rgb:abcd/", "1234/5678\x1b\\");
        await auto.TypeAsync(" unchanged", ct: token);
        await auto.WaitUntilTextAsync(draft + " unchanged");
        await auto.KeyAsync(Hex1bKey.F1, ct: token);
        await auto.WaitUntilAsync(snapshot => snapshot.GetLine(0).Trim().Equals("help", StringComparison.Ordinal));
        await auto.KeyAsync(Hex1bKey.Escape, ct: token);
        await auto.WaitUntilAsync(snapshot => !snapshot.GetLine(0).Trim().Equals("help", StringComparison.Ordinal)
            && snapshot.ContainsText(draft + " unchanged"));
        await auto.TypeAsync(" after λ", ct: token);
        await auto.WaitUntilTextAsync(draft + " unchanged after λ");
        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: token);
        await auto.WaitUntilTextAsync("console-mode-restored");
        Assert.AreEqual(draft + " unchanged after λ", await File.ReadAllTextAsync(Path.Combine(files.DirectoryPath, "draft.txt"), token));
        await auto.TypeAsync("restored λ", ct: token);
        await auto.EnterAsync(ct: token);
        await AssertCookedLineAsync(auto, files, run, token);

        async Task SendFragmentedAsync(string prefix, string suffix)
        {
            // ConPTY retains incomplete terminal responses before making console input records available.
            // Unix exposes each fragment; Windows exposes the completed response after both ordered writes.
            if (OperatingSystem.IsWindows())
            {
                await SendObservedAsync(prefix + suffix, fragments: [prefix, suffix]);
            }
            else
            {
                await SendObservedAsync(prefix);
                await SendObservedAsync(suffix);
            }
        }

        async Task SendObservedAsync(string text, bool requireMultipleReads = false, string[]? fragments = null)
        {
            var previous = Directory.EnumerateFiles(files.DirectoryPath, "*.read").ToHashSet(StringComparer.Ordinal);
            var bytes = Encoding.UTF8.GetBytes(text);
            foreach (var fragment in fragments ?? [text])
            {
                await terminal.SendInputAsync(Encoding.UTF8.GetBytes(fragment), token);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            while (true)
            {
                var paths = Directory.EnumerateFiles(files.DirectoryPath, "*.read").Where(path => !previous.Contains(path))
                    .OrderBy(path => int.Parse(Path.GetFileNameWithoutExtension(path), CultureInfo.InvariantCulture))
                    .ToArray();
                var chunks = paths.Select(TryRead).ToArray();
                if (chunks.Any(chunk => chunk is null))
                {
                    await Task.Delay(1, timeout.Token);
                    continue;
                }

                var received = chunks.SelectMany(chunk => chunk!).ToArray();
                if (received.Length >= bytes.Length)
                {
                    Assert.AreSequenceEqual(bytes, received, "The actual console reader must preserve every protocol byte in order.");
                    if (requireMultipleReads)
                    {
                        Assert.IsGreaterThan(1, paths.Length, "The long reply must span real console reads.");
                    }

                    break;
                }

                Assert.AreSequenceEqual(bytes[..received.Length], received, "Observed raw chunks must match the sent prefix.");
                await Task.Delay(1, timeout.Token);
            }
        }

        // The probe renames each chunk into place once it is written, but on Windows a file that has just appeared can be
        // held for a moment by another process. A sharing violation means the chunk is not readable yet, not that it is lost.
        static byte[]? TryRead(string path)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var chunk = new byte[stream.Length];
                stream.ReadExactly(chunk);
                return chunk;
            }
            catch (IOException) when (OperatingSystem.IsWindows())
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Successful entry, repeated entry, cancellation, and disposal preserve the original native console mode and cooked input.
    /// </summary>
    /// <param name="mode">Whether caller cancellation occurs before or during actual native raw-mode entry.</param>
    [TestMethod]
    [DataRow("normal")]
    [DataRow("cancel-before")]
    [DataRow("cancel-during")]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Probe_RestoresConsoleMode(string mode)
    {
        var token = TestContext.CancellationToken;
        using var files = new SessionWorkspaceFixture();
        await using var terminal = Create(files,
            [typeof(ConsoleStartupProbe).Assembly.Location, "--console-startup-probe", mode, files.DirectoryPath]).Build();
        var run = terminal.RunAsync(token);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(20));
        await auto.WaitUntilTextAsync("console-mode-restored");
        await auto.TypeAsync("restored λ", ct: token);
        await auto.EnterAsync(ct: token);
        await AssertCookedLineAsync(auto, files, run, token);
    }

    /// <summary>
    /// Raw reads settle before restoration and disposal, and repeated entry still accepts exact Unicode input.
    /// </summary>
    /// <param name="mode">The real read cancellation, repeated-entry, or concurrent cleanup scenario.</param>
    [TestMethod]
    [DataRow("read-exit")]
    [DataRow("read-dispose")]
    [DataRow("read-exit-dispose")]
    [DataRow("read-cancel")]
    [DataRow("read-repeat")]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Probe_SettlesReaderBeforeRestoringCookedInput(string mode)
    {
        var token = TestContext.CancellationToken;
        using var files = new SessionWorkspaceFixture();
        await using var terminal = Create(files,
            [typeof(ConsoleStartupProbe).Assembly.Location, "--console-startup-probe", mode, files.DirectoryPath]).Build();
        var run = terminal.RunAsync(token);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(20));
        await auto.WaitUntilTextAsync("raw-reader-ready");
        await auto.TypeAsync("raw λ", ct: token);
        if (mode == "read-repeat")
        {
            await auto.WaitUntilTextAsync("repeated-reader-ready");
            await auto.TypeAsync("again 日本", ct: token);
        }

        await auto.WaitUntilTextAsync("console-mode-restored");
        await auto.TypeAsync("restored λ", ct: token);
        await auto.EnterAsync(ct: token);
        await AssertCookedLineAsync(auto, files, run, token);
    }

    private async Task AssertCookedLineAsync(
        Hex1bTerminalAutomator auto,
        SessionWorkspaceFixture files,
        Task<int> run,
        CancellationToken token)
    {
        var path = Path.Combine(files.DirectoryPath, "cooked-line.txt");
        try
        {
            await auto.WaitUntilTextAsync("cooked-line:restored λ");
            Assert.AreEqual("restored λ", await File.ReadAllTextAsync(path, token));
            Assert.IsFalse(run.IsCompleted, "The probe must remain alive until its final output has been observed.");
            await File.WriteAllTextAsync(Path.Combine(files.DirectoryPath, "cooked-line.observed"), "observed", token);
            Assert.AreEqual(0, await run.WaitAsync(token));
        }
        finally
        {
            if (File.Exists(path))
            {
                TestContext.WriteLine("Actual cooked input: " + await File.ReadAllTextAsync(path, CancellationToken.None));
            }
        }
    }

    private static Hex1bTerminalBuilder Create(SessionWorkspaceFixture files, string[] arguments) =>
        Hex1bTerminal.CreateBuilder().WithPtyProcess(options =>
        {
            options.FileName = HostLocator.FindDotnet();
            options.Arguments = arguments;
            options.WorkingDirectory = files.DirectoryPath;
            options.Environment = new Dictionary<string, string> { ["TERM"] = "xterm-256color", ["NO_COLOR"] = "" };
        }).WithHeadless().WithDimensions(100, 30);
}
