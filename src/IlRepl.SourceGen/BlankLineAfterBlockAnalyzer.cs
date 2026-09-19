using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
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
        context.RegisterSyntaxNodeAction(AnalyzeStatements, SyntaxKind.Block, SyntaxKind.SwitchSection, SyntaxKind.CompilationUnit);
        context.RegisterSyntaxNodeAction(AnalyzeMembers, SyntaxKind.ClassDeclaration, SyntaxKind.StructDeclaration,
            SyntaxKind.InterfaceDeclaration, SyntaxKind.RecordDeclaration, SyntaxKind.RecordStructDeclaration);
    }

    private static void AnalyzeMembers(SyntaxNodeAnalysisContext context)
    {
        var members = ((TypeDeclarationSyntax)context.Node).Members;
        for (var index = 0; index + 1 < members.Count; index++)
        {
            if (EndsOnBraceLine(members[index]))
            {
                Check(context, members[index].GetLastToken(), members[index + 1].GetFirstToken(), codeAlso: true);
            }
        }
    }

    private static void AnalyzeStatements(SyntaxNodeAnalysisContext context)
    {
        var node = context.Node;
        IReadOnlyList<SyntaxNode> statements;
        var end = default(SyntaxToken);
        switch (node)
        {
            case BlockSyntax block:
                statements = block.Statements;
                end = block.CloseBraceToken;
                break;
            case SwitchSectionSyntax section:
                statements = section.Statements;
                break;
            default:
                statements = ((CompilationUnitSyntax)node).Members.OfType<GlobalStatementSyntax>().ToList();
                break;
        }

        for (var index = 0; index < statements.Count; index++)
        {
            if (!EndsOnBraceLine(statements[index]))
            {
                continue;
            }

            // The SDK's rule already reports a statement placed right under a block, so only what it misses is reported
            // here: a comment under any closing brace, and code under a line such as "});" that ends an expression.
            var last = statements[index].GetLastToken();
            if (index + 1 < statements.Count)
            {
                Check(context, last, statements[index + 1].GetFirstToken(), codeAlso: !last.IsKind(SyntaxKind.CloseBraceToken));
            }
            else if (!end.IsKind(SyntaxKind.None))
            {
                Check(context, last, end, codeAlso: false);
            }
        }
    }

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
