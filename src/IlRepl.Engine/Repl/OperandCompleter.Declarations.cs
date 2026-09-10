using IlRepl.Engine;
using IlRepl.Engine.Binding;
using IlRepl.Protocol;

namespace IlRepl.Repl;

public sealed partial class OperandCompleter
{
    private static bool ConfirmEnclosingType(string line, CompletionSite site, EditingView view,
        SnapshotBindingScope scope, out bool complete)
    {
        complete = false;
        var comment = false;
        var start = site.EnclosingTypeStart >= 0 ? site.EnclosingTypeStart : site.ReplaceStart;
        var declaration = CilLexer.StripComments(line[start..], ref comment);
        var position = 0;
        TypeSyntax syntax;
        try
        {
            syntax = CilSyntaxParser.ParseTypeAt(declaration, ref position);
        }
        catch (ReplException) when (start < site.ReplaceStart)
        {
            // Completing a component need not finish the enclosing generic or function-pointer syntax.
            if (site.Kind == CompletionSiteKind.TypeArgument)
            {
                return true;
            }

            comment = false;
            declaration = CilLexer.StripComments(line[site.ReplaceStart..], ref comment);
            position = 0;
            var component = SymbolBinder.BindType(CilSyntaxParser.ParseTypeAt(declaration, ref position), scope);
            return (site.Owner != ".field" || !component.Pinned) && MemberEligibility.Admits(component.Type, site, view);
        }

        complete = true;
        var declared = SymbolBinder.BindType(syntax, scope);
        var enclosing = site with
        {
            Kind = CompletionSiteKind.Type,
            ArgumentIndex = site.EnclosingTypeStart >= 0 ? site.EnclosingParameterIndex : site.ArgumentIndex,
            IsFunctionPointerReturn = false,
        };
        return (site.Owner != ".field" || !declared.Pinned) && MemberEligibility.Admits(declared.Type, enclosing, view);
    }
}
