using System.Diagnostics;
using IlRepl.Processes;
using IlRepl.Protocol;

namespace IlRepl.Tests.Protocol;

/// <summary>
/// Verifies concurrent cleanup against real hosts, supervisor processes, and filesystem failures.
/// </summary>
[TestClass]
[TestCategory("Interaction")]
public sealed class ProcessDisposalTests
{
    /// <summary>
    /// Supplies cancellation for real process and notification synchronization.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Concurrent engine cleanup waits for exit observation even when its shared lifetime was already disposed.
    /// </summary>
    /// <param name="lifetimeFirst">Whether independent lifetime cleanup completes before engine disposal.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Dispose_ConcurrentCallersJoinExitObservation(bool lifetimeFirst)
    {
        var token = TestContext.CancellationToken;
        await using var lifetime = new HostProcessLifetime();
        var engine = await lifetime.StartAsync(HostPaths.HostAssembly, RepoPaths.Root, cancellationToken: token);
        using var process = Process.GetProcessById(engine.ProcessId);
        var scope = OwnedProcessGroup.Describe(process, "disposal-host");
        using var supervisor = lifetime.SupervisorProcessId is { } supervisorId ? Process.GetProcessById(supervisorId) : null;
        var supervisorScope = supervisor is null ? null : OwnedProcessGroup.Describe(supervisor, "disposal-supervisor");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Exited += _ =>
        {
            entered.TrySetResult();
            if (!lifetimeFirst)
            {
                release.Task.GetAwaiter().GetResult();
            }
        };

        Task? first = null;
        Task? second = null;
        Task? terminated = null;
        Task? lifetimeDisposal = null;
        Task? concurrentLifetimeDisposal = null;
        try
        {
            if (lifetimeFirst)
            {
                var disposal = lifetime.DisposeAsync().AsTask();
                await entered.Task.WaitAsync(token);
                await disposal.WaitAsync(token);
                await lifetime.DisposeAsync();
                await lifetime.TerminateAsync(token);
            }

            first = engine.DisposeAsync().AsTask();
            await entered.Task.WaitAsync(token);
            if (!lifetimeFirst)
            {
                lifetimeDisposal = lifetime.DisposeAsync().AsTask();
                concurrentLifetimeDisposal = lifetime.DisposeAsync().AsTask();
            }

            second = engine.DisposeAsync().AsTask();
            terminated = engine.TerminateAsync(token);
            if (!lifetimeFirst)
            {
                Assert.IsFalse(first.IsCompleted, "Cleanup must wait for the actual exit notification to finish.");
                Assert.IsFalse(second.IsCompleted, "A concurrent disposer must join the same cleanup.");
                Assert.IsFalse(terminated.IsCompleted, "Termination during disposal must join the remaining cleanup.");
            }

            Assert.IsFalse(OwnedProcessGroup.IsRunning(scope), "The notification must describe an exited host.");
        }
        finally
        {
            release.TrySetResult();
            await engine.DisposeAsync();
            if (lifetimeDisposal is not null)
            {
                await lifetimeDisposal;
            }

            if (concurrentLifetimeDisposal is not null)
            {
                await concurrentLifetimeDisposal;
            }
        }

        await Task.WhenAll(first!, second!, terminated!).WaitAsync(token);
        await engine.DisposeAsync();
        await engine.TerminateAsync(token);
        Assert.IsFalse(OwnedProcessGroup.IsRunning(scope));
        if (supervisorScope is not null)
        {
            Assert.IsFalse(OwnedProcessGroup.IsRunning(supervisorScope));
        }
    }

    /// <summary>
    /// Every disposer and a later terminator observe endpoint cleanup failure after the real child stops.
    /// </summary>
    /// <param name="hostEndpoint">Whether the failure belongs to the host rather than its supervisor.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Dispose_ConcurrentAndLaterCallersObserveCleanupFailure(bool hostEndpoint)
    {
        if (await IsolatedTestProcess.RunAsync(TestContext))
        {
            return;
        }

        var token = TestContext.CancellationToken;
        var directory = Path.Combine("/tmp", "ilr-dispose-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);
        var originalTemp = Environment.GetEnvironmentVariable("TMPDIR");
        var lifetime = new HostProcessLifetime();
        Process? supervisor = null;
        Process? adoptedSupervisor = null;
        HostProcessEngine? failedEngine = null;
        try
        {
            Environment.SetEnvironmentVariable("TMPDIR", directory);
            await using (var engine = await lifetime.StartAsync(HostPaths.HostAssembly, RepoPaths.Root, cancellationToken: token))
            {
                supervisor = Process.GetProcessById(lifetime.SupervisorProcessId!.Value);
                await engine.HandleAsync("ldc.i4 42", token);
                var result = await engine.HandleAsync("ret", token);
                Assert.IsTrue(result.Succeeded);
            }

            var endpoints = Directory.GetDirectories(directory, "ilr-*");
            Assert.HasCount(1, endpoints, "Only the real supervisor endpoint should remain after host disposal.");
            var endpoint = endpoints[0];
            var scope = OwnedProcessGroup.Describe(supervisor, "failed-supervisor-disposal");
            if (hostEndpoint)
            {
                failedEngine = await lifetime.StartAsync(HostPaths.HostAssembly, RepoPaths.Root, cancellationToken: token);
                endpoint = Directory.GetDirectories(directory, "ilr-*").Except(endpoints).Single();
                using var process = Process.GetProcessById(failedEngine.ProcessId);
                scope = OwnedProcessGroup.Describe(process, "failed-host-disposal");
            }
            else
            {
                var epoch = lifetime.Supervision.Epoch;
                var adopted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void Observe(ProcessSupervisionState state)
                {
                    if (state.Epoch > epoch && !state.Restoring && !state.Degraded)
                    {
                        adopted.TrySetResult();
                    }
                }

                lifetime.SupervisionChanged += Observe;
                try
                {
                    supervisor.Kill();
                    await adopted.Task.WaitAsync(token);
                }
                finally
                {
                    lifetime.SupervisionChanged -= Observe;
                }

                adoptedSupervisor = Process.GetProcessById(lifetime.SupervisorProcessId!.Value);
                scope = OwnedProcessGroup.Describe(adoptedSupervisor, "later-supervisor-disposal");
            }

            Task DisposeTarget() => failedEngine is null ? lifetime.DisposeAsync().AsTask() : failedEngine.DisposeAsync().AsTask();
            var blocker = Path.Combine(endpoint, "prevent-directory-removal");
            await File.WriteAllTextAsync(blocker, "owned test file", token);
            var first = DisposeTarget();
            var second = DisposeTarget();
            var failure = await Assert.ThrowsExactlyAsync<IOException>(() => first.WaitAsync(token));
            var concurrent = await Assert.ThrowsExactlyAsync<IOException>(() => second.WaitAsync(token));
            File.Delete(blocker);
            var repeated = await Assert.ThrowsExactlyAsync<IOException>(DisposeTarget);
            var termination = await Assert.ThrowsExactlyAsync<IOException>(() => failedEngine is null
                ? lifetime.TerminateAsync(token) : failedEngine.TerminateAsync(token));
            Assert.AreSame(failure, concurrent);
            Assert.AreSame(failure, repeated);
            Assert.AreSame(failure, termination);
            Assert.IsFalse(OwnedProcessGroup.IsRunning(scope), "Endpoint deletion failure must not skip actual process cleanup.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("TMPDIR", originalTemp);
            if (failedEngine is not null)
            {
                try
                {
                    await failedEngine.DisposeAsync();
                }
                catch (IOException)
                {
                }
            }

            try
            {
                await lifetime.DisposeAsync();
            }
            catch (IOException)
            {
            }

            foreach (var child in new[] { supervisor, adoptedSupervisor }.OfType<Process>())
            {
                if (!child.HasExited)
                {
                    child.Kill();
                }

                await child.WaitForExitAsync(CancellationToken.None);
                child.Dispose();
            }

            Directory.Delete(directory, recursive: true);
        }
    }
}
