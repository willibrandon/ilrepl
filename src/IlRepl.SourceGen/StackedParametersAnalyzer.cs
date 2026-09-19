using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace IlRepl.SourceGen;

/// <summary>
/// Requires a parameter list that spans more than one line to give every parameter a line of its own.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class StackedParametersAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(DiagnosticDescriptors.ParametersAreNotStacked);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeParameters, SyntaxKind.ParameterList, SyntaxKind.BracketedParameterList);
    }

    private static void AnalyzeParameters(SyntaxNodeAnalysisContext context)
    {
        var list = (BaseParameterListSyntax)context.Node;

        // A lambda's parameters belong to the expression around them.
        if (list.Parameters.Count == 0 || list.Parent is AnonymousFunctionExpressionSyntax)
        {
            return;
        }

        var tree = list.SyntaxTree;
        var span = tree.GetLineSpan(list.Span);
        if (span.StartLinePosition.Line == span.EndLinePosition.Line)
        {
            return;
        }

        foreach (var parameter in list.Parameters)
        {
            var first = parameter.GetFirstToken();
            var previous = first.GetPreviousToken();
            var startsItsLine = tree.GetLineSpan(previous.Span).EndLinePosition.Line != tree.GetLineSpan(first.Span).StartLinePosition.Line;
            if (!startsItsLine)
            {
                context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.ParametersAreNotStacked, parameter.GetLocation(),
                    parameter.Identifier.ValueText));
            }
        }
    }
}
