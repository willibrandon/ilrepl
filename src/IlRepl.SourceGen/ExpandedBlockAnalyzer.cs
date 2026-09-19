using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace IlRepl.SourceGen;

/// <summary>
/// Requires each brace of a block to begin its own line, a lambda body included, so no block is written on one line.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ExpandedBlockAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(DiagnosticDescriptors.BlockIsNotExpanded);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeBlock, SyntaxKind.Block);
    }

    private static void AnalyzeBlock(SyntaxNodeAnalysisContext context)
    {
        var block = (BlockSyntax)context.Node;
        if (SharesLineWithPreviousToken(block.OpenBraceToken) || SharesLineWithPreviousToken(block.CloseBraceToken))
        {
            context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.BlockIsNotExpanded, block.OpenBraceToken.GetLocation()));
        }
    }

    private static bool SharesLineWithPreviousToken(SyntaxToken brace)
    {
        var previous = brace.GetPreviousToken();
        if (previous.IsKind(SyntaxKind.None))
        {
            return false;
        }

        var tree = brace.SyntaxTree!;
        return tree.GetLineSpan(previous.Span).EndLinePosition.Line == tree.GetLineSpan(brace.Span).StartLinePosition.Line;
    }
}
