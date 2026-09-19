using System;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace IlRepl.SourceGen;

/// <summary>
/// Asks for a using directive where a System type is written out in full although its short name would mean the same type.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class QualifiedNameAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(DiagnosticDescriptors.NameIsQualified);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeName, SyntaxKind.QualifiedName, SyntaxKind.SimpleMemberAccessExpression);
    }

    private static void AnalyzeName(SyntaxNodeAnalysisContext context)
    {
        var node = context.Node;
        var text = node.ToString();
        if (!text.StartsWith("System.", StringComparison.Ordinal) && !text.StartsWith("global::System.", StringComparison.Ordinal)
            || node.Ancestors().Any(ancestor => ancestor is UsingDirectiveSyntax))
        {
            return;
        }

        var left = node is QualifiedNameSyntax qualified ? qualified.Left : ((MemberAccessExpressionSyntax)node).Expression;
        var model = context.SemanticModel;
        if (model.GetSymbolInfo(node, context.CancellationToken).Symbol is not INamedTypeSymbol type
            || model.GetSymbolInfo(left, context.CancellationToken).Symbol is not INamespaceSymbol)
        {
            return;
        }

        var root = type.ContainingNamespace;
        while (root.ContainingNamespace is { IsGlobalNamespace: false } outer)
        {
            root = outer;
        }

        if (root.Name != "System")
        {
            return;
        }

        // The full name stays where the short one already means something else, such as a Mono.Cecil type or a member.
        var original = type.OriginalDefinition;
        var visible = model.LookupSymbols(node.SpanStart, name: type.Name);
        if (visible.Any(symbol => !SymbolEqualityComparer.Default.Equals(symbol.OriginalDefinition, original)))
        {
            return;
        }

        var written = node is QualifiedNameSyntax name ? name.Right.ToString() : ((MemberAccessExpressionSyntax)node).Name.ToString();
        context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.NameIsQualified, node.GetLocation(),
            type.ContainingNamespace.ToDisplayString(), written));
    }
}
