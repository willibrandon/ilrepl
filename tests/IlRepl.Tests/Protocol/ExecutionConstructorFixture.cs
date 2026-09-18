namespace IlRepl.Tests.Protocol;

/// <summary>
/// Exposes a real blocking constructor so interruption tests observe the phase from inside user object creation.
/// </summary>
public sealed class ExecutionConstructorFixture
{
    /// <summary>
    /// Waits on the independently owned test permit from inside the invoked constructor.
    /// </summary>
    public ExecutionConstructorFixture(string permit) => ExecutionThreadFixture.Wait(permit);
}
