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
        var open = block.OpenBraceToken;
        var close = block.CloseBraceToken;
        if (SharesLine(open.GetPreviousToken(), open) || SharesLine(open, open.GetNextToken())
            || SharesLine(close.GetPreviousToken(), close) || ContinuesWithCode(close))
        {
            context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.BlockIsNotExpanded, open.GetLocation()));
        }
    }

    // What closes the expression or statement around a block may follow its brace, as in "});" or "} while (more);".
    private static bool ContinuesWithCode(SyntaxToken close)
    {
        var next = close.GetNextToken();
        if (!SharesLine(close, next))
        {
            return false;
        }

        switch (next.Kind())
        {
            case SyntaxKind.CloseParenToken:
            case SyntaxKind.CloseBracketToken:
            case SyntaxKind.SemicolonToken:
            case SyntaxKind.CommaToken:
            case SyntaxKind.DotToken:
                return false;
            case SyntaxKind.WhileKeyword:
                return next.Parent is not DoStatementSyntax;
            default:
                return true;
        }
    }

    private static bool SharesLine(SyntaxToken first, SyntaxToken second)
    {
        if (first.IsKind(SyntaxKind.None) || second.IsKind(SyntaxKind.None))
        {
            return false;
        }

        var tree = first.SyntaxTree!;
        return tree.GetLineSpan(first.Span).EndLinePosition.Line == tree.GetLineSpan(second.Span).StartLinePosition.Line;
    }
}
