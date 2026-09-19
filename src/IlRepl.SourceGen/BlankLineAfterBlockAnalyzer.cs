using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace IlRepl.SourceGen;

/// <summary>
/// Requires a blank line after a closing brace where the SDK's rule is silent: before a comment, and between members.
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
        var tree = context.Node.SyntaxTree;
        for (var index = 0; index + 1 < members.Count; index++)
        {
            // An accessor list such as "{ get; set; }" ends in a brace too, but only a brace on its own line closes a body.
            var last = members[index].GetLastToken();
            if (!last.IsKind(SyntaxKind.CloseBraceToken) || SharesLine(last.GetPreviousToken(), last))
            {
                continue;
            }

            // The next member begins with its first comment, attribute, or token, whichever comes first.
            var next = members[index + 1].GetFirstToken();
            var start = next.SpanStart;
            foreach (var trivia in next.LeadingTrivia)
            {
                if (!trivia.IsKind(SyntaxKind.WhitespaceTrivia) && !trivia.IsKind(SyntaxKind.EndOfLineTrivia) && !trivia.IsDirective)
                {
                    start = trivia.SpanStart;
                    break;
                }
            }

            var line = tree.GetLineSpan(last.Span).EndLinePosition.Line;
            if (tree.GetLineSpan(new TextSpan(start, 0)).StartLinePosition.Line == line + 1)
            {
                context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.BlankLineAfterBrace,
                    Location.Create(tree, new TextSpan(start, 0))));
            }
        }
    }

    private static bool SharesLine(SyntaxToken first, SyntaxToken second)
    {
        var tree = second.SyntaxTree!;
        return !first.IsKind(SyntaxKind.None)
            && tree.GetLineSpan(first.Span).EndLinePosition.Line == tree.GetLineSpan(second.Span).StartLinePosition.Line;
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
            var last = statements[index].GetLastToken();
            if (!last.IsKind(SyntaxKind.CloseBraceToken))
            {
                continue;
            }

            // The comment belongs to whatever comes next: the following statement, or the brace that ends this list.
            var next = index + 1 < statements.Count ? statements[index + 1].GetFirstToken() : end;
            if (next.IsKind(SyntaxKind.None))
            {
                continue;
            }

            var tree = node.SyntaxTree;
            var line = tree.GetLineSpan(last.Span).EndLinePosition.Line;
            foreach (var trivia in next.LeadingTrivia)
            {
                if (!trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) && !trivia.IsKind(SyntaxKind.MultiLineCommentTrivia))
                {
                    continue;
                }

                if (tree.GetLineSpan(trivia.Span).StartLinePosition.Line == line + 1)
                {
                    context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.BlankLineAfterBrace, trivia.GetLocation()));
                }

                break;
            }
        }
    }
}
