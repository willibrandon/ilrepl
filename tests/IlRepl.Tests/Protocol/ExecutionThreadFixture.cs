using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace IlRepl.Tests.Protocol;

/// <summary>
/// Supplies real user-callable code whose state belongs to the execution thread rather than the test runner.
/// </summary>
public static class ExecutionThreadFixture
{
    [ThreadStatic]
    private static int s_threadValue;
    private static readonly ThreadLocal<int> s_local = new();
    private static readonly ConcurrentDictionary<string, ExecutionPermit> s_permits = new();

    /// <summary>
    /// Registers the permit for one real engine test without sharing mutable state with another test.
    /// </summary>
    internal static string Register(ExecutionPermit permit)
    {
        var name = Guid.NewGuid().ToString("N");
        s_permits[name] = permit;
        return name;
    }

    /// <summary>
    /// Removes a test-owned permit after its submitted method has returned.
    /// </summary>
    internal static void Unregister(string name) => s_permits.TryRemove(name, out _);

    /// <summary>
    /// Waits inside actual submitted user code until the registered permit is released.
    /// </summary>
    public static int Wait(string name) => s_permits[name].Wait();

    /// <summary>
    /// Changes thread-local values and ambient culture as a submitted cell would.
    /// </summary>
    public static string Set(int value, string name)
    {
        s_threadValue = value;
        s_local.Value = value + 1;
        Thread.CurrentThread.Name = name;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ja-JP");
        return Observe();
    }

    /// <summary>
    /// Reports all state that must survive between synchronous cells in one engine.
    /// </summary>
    public static string Observe() => string.Join('|', Environment.CurrentManagedThreadId, s_threadValue, s_local.Value,
        CultureInfo.CurrentCulture.Name, CultureInfo.CurrentUICulture.Name, Thread.CurrentThread.Name);

    /// <summary>
    /// Keeps approximately three MiB of non-tail-recursive stack frames alive until the recursion unwinds.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    public static int Recurse(int depth)
    {
        Span<byte> frame = stackalloc byte[16 * 1024];
        frame.Fill((byte)depth);
        var value = depth == 0 ? 0 : Recurse(depth - 1);
        return value + frame[depth];
    }
}
