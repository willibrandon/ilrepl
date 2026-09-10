using IlRepl.Engine;
using IlRepl.Engine.Binding;
using IlRepl.Protocol;

namespace IlRepl.Repl;

public sealed partial class OperandCompleter
{
    private static BoundInstruction? ConfirmGenericOperand(string line, CompletionSite site, EditingView view, SnapshotBindingScope scope)
    {
        var inComment = view.InBlockComment;
        var normalized = CilLexer.StripComments(line, ref inComment).Trim();
        var (_, text) = InstructionParser.SplitLabels(normalized);
        if (site.Owner is ".dis" or ".disassemble")
        {
            var end = 0;
            while (end < text.Length && !char.IsWhiteSpace(text[end]))
            {
                end++;
            }

            text = "call" + text[end..];
        }

        InstructionSyntax syntax;
        try
        {
            syntax = CilSyntaxParser.ParseInstruction(text);
        }
        catch (ReplException)
        {
            // A declaring construction or method argument can be edited before the member reference is finished.
            return null;
        }

        if (syntax.Operand.Member is { } member)
        {
            foreach (var type in new[] { member.DeclaringType, member.ReturnType }.OfType<TypeSyntax>()
                .Concat(member.GenericArguments ?? []))
            {
                SymbolBinder.BindType(type, scope);
            }

            if (member.Parameters is null && syntax.Operand.Kind != OperandSyntaxKind.Field && !syntax.Operand.IsFieldToken)
            {
                if (member.GenericArguments is not null && !HasGenericTarget(member, site, view, scope))
                {
                    throw new ReplException("no eligible method accepts the completed generic arguments");
                }

                return null;
            }
        }

        var instruction = SymbolBinder.BindInstruction(syntax, scope);
        var operand = instruction.Operand;
        if (operand.Type is { } operandType && !MemberEligibility.Admits(operandType,
            site with { Kind = CompletionSiteKind.Type, IsFunctionPointerReturn = false }, view)
            || operand.Method is { } method && !MemberEligibility.Admits(method.Method, site, view)
            || operand.Field is { } field && !MemberEligibility.Admits(field, site, view))
        {
            throw new ReplException("the completed operand is not valid at this instruction");
        }

        return instruction;
    }

    private static bool HasGenericTarget(MemberSyntax member, CompletionSite site, EditingView view, SnapshotBindingScope scope)
    {
        var arguments = member.GenericArguments!.Select(argument => SymbolBinder.BindType(argument, scope).Type).ToArray();
        var methods = member.DeclaringType is { } declaring
            ? scope.AllMethods(SymbolBinder.BindType(declaring, scope).Type) : scope.SessionMethods;
        foreach (var method in methods.Where(method => method.Name == member.Name && method.Arity == arguments.Length))
        {
            try
            {
                if (scope.Instantiate(method, arguments) is { } constructed && MemberEligibility.Admits(constructed, site, view))
                {
                    return true;
                }
            }
            catch (Exception exception) when (ReplRecovery.IsRecoverable(exception))
            {
                // A different overload may accept these arguments once its parameter list is supplied.
            }
        }

        return false;
    }
}
