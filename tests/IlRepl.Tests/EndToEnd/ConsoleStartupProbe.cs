using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
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
        // Cooked Console.In otherwise converts Windows input through the inherited legacy code page.
        // Establish the Unicode contract before capturing the modes that the presentation must restore.
        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = Encoding.UTF8;
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
        else if (mode.StartsWith("read-", StringComparison.Ordinal))
        {
            await ReadLifecycleAsync(mode, original, token);
        }
        else
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            using var stopMonitor = new CancellationTokenSource();
            using var monitorReady = new ManualResetEventSlim();
            Exception? monitorError = null;
            Thread? monitor = null;
            if (mode == "cancel-before")
            {
                cancellation.Cancel();
            }

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
                    catch (Exception exception)
                    {
                        monitorError = exception;
                        cancellation.Cancel();
                    }
                }) { IsBackground = true };
                monitor.Start();
                monitorReady.Wait(token);
            }

            var cancelled = false;
            try
            {
                await using var presentation = new ConsolePresentation();
                try
                {
                    await presentation.EnterRawModeAsync(cancellation.Token);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    cancelled = true;
                }

                if (OperatingSystem.IsWindows() && mode == "cancel-during" && !cancelled)
                {
                    // The Windows driver has no asynchronous capability probe. Keep its entered native mode observable
                    // until cancellation is acknowledged, then prove that the cancelled caller cannot re-enter it.
                    monitor!.Join();
                    if (!cancellation.IsCancellationRequested || CaptureMode().SequenceEqual(original))
                    {
                        throw new InvalidOperationException("Windows raw-mode cancellation was not observed.");
                    }

                    try
                    {
                        await presentation.EnterRawModeAsync(cancellation.Token);
                    }
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                    {
                        cancelled = true;
                    }
                }

                if (mode == "normal")
                {
                    if (CaptureMode().SequenceEqual(original))
                    {
                        throw new InvalidOperationException("Raw mode was never entered.");
                    }

                    await presentation.ExitRawModeAsync(token);
                    RequireRestored(original);
                    await presentation.EnterRawModeAsync(token);
                    if (CaptureMode().SequenceEqual(original))
                    {
                        throw new InvalidOperationException("Repeated raw-mode entry failed.");
                    }
                }
            }
            finally
            {
                stopMonitor.Cancel();
                monitor?.Join();
            }

            if (monitorError is not null)
            {
                throw new InvalidOperationException("Console-mode observation failed.", monitorError);
            }

            if (cancelled != mode.StartsWith("cancel-", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Caller cancellation was not preserved.");
            }
        }

        RequireRestored(original);
        Console.WriteLine("console-mode-restored");
        var line = await Console.In.ReadLineAsync(token);
        await File.WriteAllTextAsync(Path.Combine(directory, "cooked-line.txt"), line, token);
        Console.WriteLine("cooked-line:" + line);
        // PTY process exit can stop the parent's output pump before it applies the final bytes.
        // Retain the actual input and stay alive until the parent observes the complete rendered line.
        while (!File.Exists(Path.Combine(directory, "cooked-line.observed")))
        {
            await Task.Delay(1, token);
        }

        return line == "restored λ" ? 0 : 1;
    }

    private static async Task ReadLifecycleAsync(string mode, byte[] original, CancellationToken token)
    {
        await using var presentation = new ConsolePresentation();
        await presentation.EnterRawModeAsync(token);
        await presentation.ExitRawModeAsync(token);
        RequireRestored(original);
        await presentation.EnterRawModeAsync(token);
        Console.WriteLine("raw-reader-ready");
        await ReadTextAsync(presentation, "raw λ", token);

        using var caller = CancellationTokenSource.CreateLinkedTokenSource(token);
        var pending = presentation.ReadInputAsync(caller.Token).AsTask();
        if (pending.IsCompleted)
        {
            throw new InvalidOperationException("The real console read was not pending.");
        }

        if (mode == "read-cancel")
        {
            await caller.CancelAsync();
            await pending.WaitAsync(token);
            pending = presentation.ReadInputAsync(token).AsTask();
        }

        var queued = presentation.ReadInputAsync(token).AsTask();
        if (queued.IsCompleted)
        {
            throw new InvalidOperationException("The second console read was not pending.");
        }

        if (mode == "read-dispose")
        {
            await Task.WhenAll(presentation.DisposeAsync().AsTask(), presentation.DisposeAsync().AsTask()).WaitAsync(token);
        }
        else if (mode == "read-exit-dispose")
        {
            await Task.WhenAll(presentation.ExitRawModeAsync(token).AsTask(), presentation.DisposeAsync().AsTask(),
                presentation.DisposeAsync().AsTask()).WaitAsync(token);
        }
        else
        {
            await Task.WhenAll(presentation.ExitRawModeAsync(token).AsTask(), presentation.ExitRawModeAsync(token).AsTask());
        }

        if (!pending.IsCompleted || !queued.IsCompleted)
        {
            throw new InvalidOperationException("Console restoration returned before its readers settled.");
        }

        if (!(await pending).IsEmpty || !(await queued).IsEmpty)
        {
            throw new InvalidOperationException("A stopped reader consumed unexpected input.");
        }

        RequireRestored(original);

        if (mode == "read-repeat")
        {
            await presentation.EnterRawModeAsync(token);
            Console.WriteLine("repeated-reader-ready");
            await ReadTextAsync(presentation, "again 日本", token);
            pending = presentation.ReadInputAsync(token).AsTask();
            await presentation.ExitRawModeAsync(token);
            if (!pending.IsCompleted)
            {
                throw new InvalidOperationException("Repeated restoration left an active reader.");
            }

            await pending;
            RequireRestored(original);
        }
    }

    private static async Task ReadTextAsync(ConsolePresentation presentation, string expected, CancellationToken token)
    {
        var bytes = new List<byte>();
        var length = Encoding.UTF8.GetByteCount(expected);
        while (bytes.Count < length)
        {
            var next = await presentation.ReadInputAsync(token);
            if (next.IsEmpty)
            {
                throw new InvalidOperationException("The raw console reader stopped before receiving its input.");
            }

            bytes.AddRange(next.ToArray());
        }

        if (Encoding.UTF8.GetString(bytes.ToArray()) != expected)
        {
            throw new InvalidOperationException("The real console reader changed its input.");
        }
    }

    private static void RequireRestored(byte[] original)
    {
        if (!CaptureMode().SequenceEqual(original))
        {
            throw new InvalidOperationException("The original console mode was not restored.");
        }
    }

    private static unsafe byte[] CaptureMode()
    {
        if (OperatingSystem.IsWindows())
        {
            if (GetConsoleMode(GetStdHandle(-10), out var input) == 0 || GetConsoleMode(GetStdHandle(-11), out var output) == 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            return [.. BitConverter.GetBytes(input), .. BitConverter.GetBytes(output)];
        }

        // A zeroed opaque buffer exceeds termios on the supported Unix targets and compares every native flag without assuming layout.
        var attributes = new byte[256];
        fixed (byte* address = attributes)
        {
            if (GetTerminalAttributes(0, address) != 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
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
