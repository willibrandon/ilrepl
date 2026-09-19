namespace IlRepl.Tests.Engine;

/// <summary>
/// Defines every input consumed by an isolated exported-method invocation.
/// </summary>
/// <param name="ImagePath">The assembly image to execute.</param>
/// <param name="Type">The declaring type's reflection name.</param>
/// <param name="Method">The static entry method name.</param>
/// <param name="Arguments">The typed arguments supplied to the method.</param>
/// <param name="GenericArguments">The assembly-qualified generic argument types.</param>
/// <param name="Culture">The initial culture.</param>
/// <param name="UICulture">The initial resource lookup culture.</param>
/// <param name="StandardInput">The input followed by an explicit end of stream.</param>
/// <param name="InputEncoding">The encoding used to decode the standard input payload.</param>
/// <param name="Environment">The complete environment applied before runtime startup.</param>
/// <param name="Profile">The declared runtime compilation profile.</param>
/// <param name="WorkingDirectory">The isolated working directory.</param>
/// <param name="Dependencies">The dependency assembly paths available to the isolated loader.</param>
/// <param name="InitialFiles">The initial relative filesystem contents restored before each artifact.</param>
/// <param name="StackSize">The explicitly requested execution-thread stack size in bytes.</param>
/// <param name="EndOfInput">Whether the supplied input is immediately followed by EOF.</param>
internal sealed record ExportRequest(
    string ImagePath,
    string Type,
    string Method,
    ExportValue[] Arguments,
    string[] GenericArguments,
    string Culture,
    string UICulture,
    byte[] StandardInput,
    string InputEncoding,
    Dictionary<string, string> Environment,
    string Profile,
    string WorkingDirectory,
    string[] Dependencies,
    Dictionary<string, string> InitialFiles,
    int StackSize,
    bool EndOfInput);
