namespace IlRepl.Tests.Shared;

/// <summary>
/// Describes a source reproduction with explicit verification and execution expectations.
/// </summary>
/// <param name="Name">The unique method name.</param>
/// <param name="Body">The method body, with explicit IL exception transitions.</param>
/// <param name="Accepted">Whether the source is correct and can execute.</param>
/// <param name="Finding">A diagnostic fragment required when the source is refused.</param>
/// <param name="Unverifiable">Whether verification fails even though the source is correct.</param>
/// <param name="GenericParameters">The optional method parameter declarations.</param>
/// <param name="GenericArguments">The corresponding closed type arguments used for execution.</param>
/// <param name="Verification">The independent verifier diagnostic codes, separated by commas.</param>
/// <param name="VerificationFailure">An exact documented unsupported operation in the independent verifier.</param>
/// <param name="Implementation">Optional method implementation attributes written after the parameter list.</param>
/// <param name="Members">Optional members declared before the method in its containing type.</param>
/// <param name="Declarations">Optional top-level types declared before the method.</param>
/// <param name="Input">The integer argument supplied to every execution path.</param>
/// <param name="Expected">The integer result required from every execution path.</param>
/// <param name="BrowserCompatible">Whether the fixture can execute under the browser Mono interpreter.</param>
/// <param name="ExportVerification">Export-specific verifier codes, or null to use the independent fixture's codes.</param>
public sealed record ControlFlowExample(
    string Name,
    string[] Body,
    bool Accepted,
    string Finding = "",
    bool Unverifiable = false,
    string Verification = "",
    string GenericParameters = "",
    string GenericArguments = "",
    string VerificationFailure = "",
    string Implementation = "",
    string Members = "",
    string Declarations = "",
    int Input = 1,
    int Expected = 42,
    string? ExportVerification = null,
    bool BrowserCompatible = true)
{
    /// <summary>
    /// The complete declaration entered at the prompt.
    /// </summary>
    public string Source => DeclarationPrefix + (GenericParameters.Length == 0
        ? $".method int32 {Name}(int32 n){ImplementationSuffix} {{\n" + string.Join('\n', Body) + "\n}"
        : $".class public FlowGeneric {{\n{MemberPrefix}"
            + $".method public static int32 {Name}{GenericHeader}(int32 n){ImplementationSuffix} {{\n"
            + string.Join('\n', Body) + "\n}\n}");

    private string ImplementationSuffix => Implementation.Length == 0 ? "" : " " + Implementation;
    private string MemberPrefix => Members.Length == 0 ? "" : Members + "\n";
    private string DeclarationPrefix => Declarations.Length == 0 ? "" : Declarations + "\n";

    /// <summary>
    /// The method's generic declaration suffix, including its constraints.
    /// </summary>
    public string GenericHeader => GenericParameters.Length == 0 ? "" : "<" + GenericParameters + ">";

    /// <summary>
    /// The complete call operand for this example's closed instantiation.
    /// </summary>
    public string Call => "call int32 " + (GenericParameters.Length == 0 ? "" : "FlowGeneric::") + Name
        + (GenericArguments.Length == 0 ? "" : "<" + GenericArguments + ">") + "(int32)";

}
