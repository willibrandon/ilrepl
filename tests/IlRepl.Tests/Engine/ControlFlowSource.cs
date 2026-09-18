using IlRepl.Tests.Shared;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Produces an independently authored ILAsm fixture from the corpus without using an ilrepl renderer.
/// </summary>
internal static class ControlFlowSource
{
    /// <summary>
    /// Creates the original fixture declaration for independent assembly and verification.
    /// </summary>
    /// <param name="example">The shared source and expected result.</param>
    /// <returns>The complete independent ILAsm declaration.</returns>
    internal static string Original(ControlFlowExample example)
    {
        var implementation = example.Implementation.Length == 0 ? "" : " " + example.Implementation;
        var members = example.Members.Length == 0 ? ""
            : example.Members.Replace("FlowGeneric::", "Fixture::", StringComparison.Ordinal)
                .Replace("\n", "\n    ", StringComparison.Ordinal) + "\n    ";
        var declarations = example.Declarations.Length == 0 ? ""
            : example.Declarations.Replace("} handler {", "} {", StringComparison.Ordinal) + "\n";
        var body = string.Join('\n', example.Body).Replace("} handler {", "} {", StringComparison.Ordinal)
            .Replace("FlowGeneric::", "Fixture::", StringComparison.Ordinal);
        return $$"""
            .assembly extern System.Runtime { }
            .assembly extern System.Private.CoreLib { }
            .assembly FlowCorpus { }
            .module FlowCorpus.dll
            {{declarations}}
            .class public Fixture extends [System.Runtime]System.Object {
                {{members}}.method public static int32 {{example.Name}}{{example.GenericHeader}}(int32 n) cil managed{{implementation}} {
                    .maxstack 64
                    {{body}}
                }
            }
            """;
    }
}
