using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace IlRepl.SourceGen;

/// <summary>
/// Requires a blank line after a closing brace where the SDK's rule is silent: comments, members, and lines such as "});".
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class BlankLineAfterBlockAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(DiagnosticDescriptors.BlankLineAfterBrace);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxTreeAction(AnalyzeComments);
        context.RegisterSyntaxNodeAction(AnalyzeSequence, SyntaxKind.Block, SyntaxKind.SwitchSection, SyntaxKind.CompilationUnit,
            SyntaxKind.NamespaceDeclaration, SyntaxKind.FileScopedNamespaceDeclaration, SyntaxKind.ClassDeclaration,
            SyntaxKind.StructDeclaration, SyntaxKind.InterfaceDeclaration, SyntaxKind.RecordDeclaration,
            SyntaxKind.RecordStructDeclaration);
    }

    // A comment is held to the rule wherever it follows a closing brace line: between statements, members, accessors, or
    // switch sections, before an "else", "catch", "finally", or "while", and at the end of a body or the file. Reading
    // the tokens covers them all, because no list of syntax nodes names every place a comment can stand.
    private static void AnalyzeComments(SyntaxTreeAnalysisContext context)
    {
        foreach (var token in context.Tree.GetRoot(context.CancellationToken).DescendantTokens())
        {
            if (!token.IsKind(SyntaxKind.CloseBraceToken) || SharesLine(token.GetPreviousToken(), token))
            {
                continue;
            }

            var last = token;
            for (var next = last.GetNextToken(); SharesLine(last, next); next = last.GetNextToken())
            {
                last = next;
            }

            if (FindComment(last, last.GetNextToken(includeZeroWidth: true)) is { } comment)
            {
                context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.BlankLineAfterBrace, comment.GetLocation()));
            }
        }
    }

    private static void AnalyzeSequence(SyntaxNodeAnalysisContext context)
    {
        IReadOnlyList<SyntaxNode> items = context.Node switch
        {
            BlockSyntax block => block.Statements,
            SwitchSectionSyntax section => section.Statements,
            CompilationUnitSyntax unit => unit.Members,
            BaseNamespaceDeclarationSyntax space => space.Members,
            _ => ((TypeDeclarationSyntax)context.Node).Members,
        };

        for (var index = 0; index + 1 < items.Count; index++)
        {
            if (!EndsOnBraceLine(items[index]))
            {
                continue;
            }

            // The SDK's rule already reports a statement placed right under a block, so that one case is left to it.
            var last = items[index].GetLastToken();
            var next = items[index + 1];
            if (last.IsKind(SyntaxKind.CloseBraceToken) && IsStatement(items[index]) && IsStatement(next))
            {
                continue;
            }

            // A comment under the brace is the other pass's to report.
            var first = next.GetFirstToken();
            if (!HasComment(first) && first.SyntaxTree!.GetLineSpan(first.Span).StartLinePosition.Line == LineOf(last) + 1)
            {
                context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.BlankLineAfterBrace, first.GetLocation()));
            }
        }
    }

    private static bool IsStatement(SyntaxNode node) => node is StatementSyntax or GlobalStatementSyntax;

    // A body, a lambda, or an initializer can end a statement or member on a line that begins with its closing brace.
    private static bool EndsOnBraceLine(SyntaxNode node)
    {
        var token = node.GetLastToken();
        if (node.SyntaxTree.GetLineSpan(node.Span).StartLinePosition.Line == LineOf(token))
        {
            return false;
        }

        while (SharesLine(token.GetPreviousToken(), token))
        {
            token = token.GetPreviousToken();
        }

        return token.IsKind(SyntaxKind.CloseBraceToken);
    }

    // The comment on the line right under the one "last" ends, when a comment is the first thing written there.
    private static SyntaxTrivia? FindComment(SyntaxToken last, SyntaxToken next)
    {
        foreach (var trivia in next.LeadingTrivia)
        {
            if (IsLayout(trivia))
            {
                continue;
            }

            var line = trivia.SyntaxTree!.GetLineSpan(trivia.Span).StartLinePosition.Line;
            return line == LineOf(last) + 1 ? trivia : null;
        }

        return null;
    }

    private static bool HasComment(SyntaxToken token)
    {
        foreach (var trivia in token.LeadingTrivia)
        {
            if (!IsLayout(trivia))
            {
                return true;
            }
        }

        return false;
    }

    // A directive such as "#endif" separates the brace from what follows, as a blank line does.
    private static bool IsLayout(SyntaxTrivia trivia) =>
        trivia.IsKind(SyntaxKind.WhitespaceTrivia) || trivia.IsKind(SyntaxKind.EndOfLineTrivia) || trivia.IsDirective;

    private static int LineOf(SyntaxToken token) => token.SyntaxTree!.GetLineSpan(token.Span).EndLinePosition.Line;

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
