using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace IlRepl.SourceGen;

/// <summary>
/// Requires each brace of a block, type, namespace, or switch to stand alone on its line, a lambda body included.
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
        context.RegisterSyntaxNodeAction(AnalyzeBlock, SyntaxKind.Block, SyntaxKind.SwitchStatement, SyntaxKind.NamespaceDeclaration,
            SyntaxKind.ClassDeclaration, SyntaxKind.StructDeclaration, SyntaxKind.InterfaceDeclaration, SyntaxKind.EnumDeclaration,
            SyntaxKind.RecordDeclaration, SyntaxKind.RecordStructDeclaration);
    }

    private static void AnalyzeBlock(SyntaxNodeAnalysisContext context)
    {
        SyntaxToken open;
        SyntaxToken close;
        switch (context.Node)
        {
            case BlockSyntax block:
                open = block.OpenBraceToken;
                close = block.CloseBraceToken;
                break;
            case SwitchStatementSyntax choice:
                open = choice.OpenBraceToken;
                close = choice.CloseBraceToken;
                break;
            case NamespaceDeclarationSyntax space:
                open = space.OpenBraceToken;
                close = space.CloseBraceToken;
                break;
            default:
                var type = (BaseTypeDeclarationSyntax)context.Node;
                open = type.OpenBraceToken;
                close = type.CloseBraceToken;
                break;
        }

        // A record or type declared with a semicolon has no braces to place.
        if (open.IsKind(SyntaxKind.None) || open.IsMissing)
        {
            return;
        }

        if (SharesLine(open.GetPreviousToken(), open) || SharesLine(open, open.GetNextToken())
            || SharesLine(close.GetPreviousToken(), close) || ContinuesWithCode(close)
            || HasCommentBeside(open) || HasCommentBeside(close))
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

    // A comment is trivia, so the neighbouring tokens do not show it.
    private static bool HasCommentBeside(SyntaxToken brace)
    {
        var tree = brace.SyntaxTree!;
        var line = tree.GetLineSpan(brace.Span).StartLinePosition.Line;
        foreach (var trivia in brace.LeadingTrivia.Concat(brace.TrailingTrivia))
        {
            if (IsComment(trivia) && tree.GetLineSpan(trivia.Span).EndLinePosition.Line == line)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsComment(SyntaxTrivia trivia) =>
        trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia);

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
