namespace IlRepl.Tests;

/// <summary>
/// Collects everything that can be collected, for tests that assert an assembly or an object was released.
/// </summary>
internal static class FullCollection
{
    /// <summary>
    /// Waits for a complete collection, including the finalizers it queues, before it returns.
    /// </summary>
    /// <remarks>
    /// The runtime repeats full collections and finalization until the heap size settles, which is what releasing a
    /// collectible assembly takes. Product code never asks for a collection; only a test about lifetimes has a reason to.
    /// </remarks>
    internal static void Run() => GC.GetTotalMemory(forceFullCollection: true);
}
