using System.ComponentModel;
using System.Runtime.InteropServices;
using Hex1b;
using IlRepl.Protocol;
using IlRepl.Repl;
using IlRepl.Tui;

namespace IlRepl.Tests.EndToEnd;

/// <summary>
/// Exercises actual console initialization and restoration in a disposable pseudoterminal child.
/// </summary>
internal static partial class ConsoleStartupProbe
{
    /// <summary>
    /// Runs a real editor or observes native console mode before, during, and after a cancelled capability probe.
    /// </summary>
    internal static async Task<int> RunAsync(string mode, string directory)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        var original = CaptureMode();
        if (mode == "editor")
        {
            await using var engine = new InProcessEngine();
            PromptState? prompt = null;
            await using var terminal = IlReplApp.Configure(Hex1bTerminal.CreateBuilder(), engine, new Transcript(),
                onPrompt: value => prompt = value).WithPresentation(new ObservedConsolePresentation(directory)).WithMouse().Build();
            await IlReplApp.RunAsync(terminal, prompt, token);
            await File.WriteAllTextAsync(Path.Combine(directory, "draft.txt"), prompt!.Text, token);
        }
        else
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            using var stopMonitor = new CancellationTokenSource();
            using var monitorReady = new ManualResetEventSlim();
            Exception? monitorError = null;
            Thread? monitor = null;
            if (mode == "cancel-before") cancellation.Cancel();
            if (mode == "cancel-during")
            {
                monitor = new Thread(() =>
                {
                    monitorReady.Set();
                    try
                    {
                        while (!stopMonitor.IsCancellationRequested && !token.IsCancellationRequested)
                        {
                            if (!CaptureMode().SequenceEqual(original))
                            {
                                cancellation.Cancel();
                                return;
                            }
                            Thread.Yield();
                        }
                    }
                    catch (Exception exception) { monitorError = exception; cancellation.Cancel(); }
                }) { IsBackground = true };
                monitor.Start();
                monitorReady.Wait(token);
            }

            var cancelled = false;
            try
            {
                await using var presentation = new ConsolePresentation();
                try { await presentation.EnterRawModeAsync(cancellation.Token); }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { cancelled = true; }
                if (mode == "normal")
                {
                    if (CaptureMode().SequenceEqual(original)) throw new InvalidOperationException("Raw mode was never entered.");
                    await presentation.ExitRawModeAsync(token);
                    RequireRestored(original);
                    await presentation.EnterRawModeAsync(token);
                    if (CaptureMode().SequenceEqual(original)) throw new InvalidOperationException("Repeated raw-mode entry failed.");
                }
            }
            finally
            {
                stopMonitor.Cancel();
                monitor?.Join();
            }

            if (monitorError is not null) throw new InvalidOperationException("Console-mode observation failed.", monitorError);
            if (cancelled != mode.StartsWith("cancel-", StringComparison.Ordinal))
                throw new InvalidOperationException("Caller cancellation was not preserved.");
        }

        RequireRestored(original);
        Console.WriteLine("console-mode-restored");
        var line = await Console.In.ReadLineAsync(token);
        Console.WriteLine("cooked-line:" + line);
        return line == "restored λ" ? 0 : 1;
    }

    private static void RequireRestored(byte[] original)
    {
        if (!CaptureMode().SequenceEqual(original)) throw new InvalidOperationException("The original console mode was not restored.");
    }

    private static unsafe byte[] CaptureMode()
    {
        if (OperatingSystem.IsWindows())
        {
            if (GetConsoleMode(GetStdHandle(-10), out var input) == 0 || GetConsoleMode(GetStdHandle(-11), out var output) == 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            return [.. BitConverter.GetBytes(input), .. BitConverter.GetBytes(output)];
        }

        // A zeroed opaque buffer exceeds termios on the supported Unix targets and compares every native flag without assuming layout.
        var attributes = new byte[256];
        fixed (byte* address = attributes)
        {
            if (GetTerminalAttributes(0, address) != 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        return attributes;
    }

    [LibraryImport("libc", EntryPoint = "tcgetattr", SetLastError = true)]
    private static unsafe partial int GetTerminalAttributes(int descriptor, byte* attributes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint GetStdHandle(int identifier);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int GetConsoleMode(nint handle, out uint mode);
}
