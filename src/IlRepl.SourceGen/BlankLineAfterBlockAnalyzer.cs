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
        context.RegisterSyntaxNodeAction(AnalyzeSequence, SyntaxKind.Block, SyntaxKind.SwitchSection, SyntaxKind.CompilationUnit,
            SyntaxKind.NamespaceDeclaration, SyntaxKind.FileScopedNamespaceDeclaration, SyntaxKind.ClassDeclaration,
            SyntaxKind.StructDeclaration, SyntaxKind.InterfaceDeclaration, SyntaxKind.RecordDeclaration,
            SyntaxKind.RecordStructDeclaration, SyntaxKind.AccessorList);
    }

    private static void AnalyzeSequence(SyntaxNodeAnalysisContext context)
    {
        IReadOnlyList<SyntaxNode> items = context.Node switch
        {
            BlockSyntax block => block.Statements,
            SwitchSectionSyntax section => section.Statements,
            CompilationUnitSyntax unit => unit.Members,
            BaseNamespaceDeclarationSyntax space => space.Members,
            AccessorListSyntax accessors => accessors.Accessors,
            _ => ((TypeDeclarationSyntax)context.Node).Members,
        };

        for (var index = 0; index < items.Count; index++)
        {
            if (!EndsOnBraceLine(items[index]))
            {
                continue;
            }

            var last = items[index].GetLastToken();
            if (index + 1 == items.Count)
            {
                // Only a comment can stand between the last item and what ends its container: a brace, the next case, or the file.
                Check(context, last, last.GetNextToken(includeZeroWidth: true), codeAlso: false);
                continue;
            }

            // The SDK's rule already reports a statement placed right under a block, so that one case is left to it.
            // Accessors stay together, which leaves only a comment under one of them to report.
            var next = items[index + 1];
            var sdkReports = last.IsKind(SyntaxKind.CloseBraceToken) && IsStatement(items[index]) && IsStatement(next);
            Check(context, last, next.GetFirstToken(), codeAlso: !sdkReports && context.Node is not AccessorListSyntax);
        }
    }

    private static bool IsStatement(SyntaxNode node) => node is StatementSyntax or GlobalStatementSyntax;

    // A body, a lambda, or an initializer can end a statement or member on a line that begins with its closing brace.
    private static bool EndsOnBraceLine(SyntaxNode node)
    {
        var tree = node.SyntaxTree;
        var token = node.GetLastToken();
        var line = tree.GetLineSpan(token.Span).StartLinePosition.Line;
        if (tree.GetLineSpan(node.Span).StartLinePosition.Line == line)
        {
            return false;
        }

        while (true)
        {
            var previous = token.GetPreviousToken();
            if (previous.IsKind(SyntaxKind.None) || tree.GetLineSpan(previous.Span).EndLinePosition.Line != line)
            {
                return token.IsKind(SyntaxKind.CloseBraceToken);
            }

            token = previous;
        }
    }

    private static void Check(SyntaxNodeAnalysisContext context, SyntaxToken last, SyntaxToken next, bool codeAlso)
    {
        var tree = last.SyntaxTree!;
        var line = tree.GetLineSpan(last.Span).EndLinePosition.Line;
        foreach (var trivia in next.LeadingTrivia)
        {
            if (trivia.IsKind(SyntaxKind.WhitespaceTrivia) || trivia.IsKind(SyntaxKind.EndOfLineTrivia) || trivia.IsDirective)
            {
                continue;
            }

            if (tree.GetLineSpan(trivia.Span).StartLinePosition.Line == line + 1)
            {
                context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.BlankLineAfterBrace, trivia.GetLocation()));
            }

            return;
        }

        if (codeAlso && tree.GetLineSpan(next.Span).StartLinePosition.Line == line + 1)
        {
            context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.BlankLineAfterBrace, next.GetLocation()));
        }
    }
}
