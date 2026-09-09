namespace Greeter;

/// <summary>
/// A type with members of every access a derived session type and a cell see differently.
/// </summary>
public class Account
{
    /// <summary>
    /// The balance, visible to derived types.
    /// </summary>
    protected int Balance;

    /// <summary>
    /// What the last deposit audited to.
    /// </summary>
    public int LastAudit;

    /// <summary>
    /// Adds to the balance and audits it.
    /// </summary>
    /// <param name="amount">The amount.</param>
    public void Deposit(int amount)
    {
        Post(amount);
        LastAudit = Audit() + Secret();
    }

    /// <summary>
    /// Posts an amount; derived types may change how.
    /// </summary>
    /// <param name="amount">The amount.</param>
    protected virtual void Post(int amount) => Balance += amount;

    private int Audit() => Balance;

    private static int Secret() => 42;
}
