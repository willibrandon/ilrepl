using Microsoft.CodeAnalysis;

namespace IlRepl.SourceGen;

/// <summary>
/// The layout rules this repository enforces beyond those the .NET SDK provides.
/// </summary>
internal static class DiagnosticDescriptors
{
    /// <summary>
    /// A block, type, namespace, or switch is never written on one line, and each of its braces stands alone on its line.
    /// </summary>
    internal static readonly DiagnosticDescriptor BlockIsNotExpanded = new(
        id: "ILREPL0001",
        title: "A block's braces stand on their own lines",
        messageFormat: "Put this block's braces on their own lines",
        category: "IlRepl.Layout",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>
    /// A parameter list that does not fit on one line gives every parameter a line of its own.
    /// </summary>
    internal static readonly DiagnosticDescriptor ParametersAreNotStacked = new(
        id: "ILREPL0002",
        title: "Parameters that wrap are stacked one to a line",
        messageFormat: "Put parameter '{0}' on its own line, because this parameter list spans more than one line",
        category: "IlRepl.Layout",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>
    /// A System type is named through a using directive unless its short name would mean something else where it is written.
    /// </summary>
    internal static readonly DiagnosticDescriptor NameIsQualified = new(
        id: "ILREPL0004",
        title: "A System type is imported instead of written out in full",
        messageFormat: "Import '{0}' with a using directive and write '{1}'",
        category: "IlRepl.Layout",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>
    /// A closing brace is followed by a blank line before the comment or member that comes next.
    /// </summary>
    internal static readonly DiagnosticDescriptor BlankLineAfterBrace = new(
        id: "ILREPL0005",
        title: "A closing brace is followed by a blank line",
        messageFormat: "Leave a blank line after the closing brace above",
        category: "IlRepl.Layout",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>
    /// A line stays within the max_line_length set in the editor configuration.
    /// </summary>
    internal static readonly DiagnosticDescriptor LineIsTooLong = new(
        id: "ILREPL0003",
        title: "A line fits within max_line_length",
        messageFormat: "This line is {0} characters long; the limit is {1}",
        category: "IlRepl.Layout",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}
