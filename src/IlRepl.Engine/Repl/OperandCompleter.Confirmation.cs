using IlRepl.Engine;
using IlRepl.Engine.Binding;
using IlRepl.Protocol;

namespace IlRepl.Repl;

public sealed partial class OperandCompleter
{
    private CompletionItem? Confirm(CompletionQuery query, OperandCandidate candidate)
    {
        try
        {
            var site = query.Identity.Site;
            var scope = ((SnapshotBindingScope)query.View.Scope).ForConfirmation();
            if (candidate.StartsGeneric)
            {
                return GenericStarter(query, candidate);
            }

            var insertion = Insertion(query, candidate);
            if (insertion is null)
            {
                return null;
            }

            var original = query.Identity.Document.Lines[query.Identity.Document.Line];
            var line = original[..site.ReplaceStart] + insertion + original[site.ReplaceEnd..];
            if (candidate.Type is not null && site.Kind == CompletionSiteKind.Type && site.Owner is ".locals" or ".args" or ".method")
            {
                var position = site.ReplaceStart;
                var declaredType = SymbolBinder.BindType(CilSyntaxParser.ParseTypeAt(line, ref position), scope).Type;
                if (!MemberEligibility.Admits(declaredType, site, query.View))
                {
                    return null;
                }
            }

            var continues = candidate.Type is not null && candidate.Slot < 0
                && site.Kind == CompletionSiteKind.MemberHead && !site.NextIsDoubleColon;
            BoundInstruction? instruction = null;
            if (!continues && site.Kind != CompletionSiteKind.TypeArgument)
            {
                if (site.Owner.StartsWith('.') || site.Owner is "extends" or "implements" or "catch")
                {
                    if (site.DeclarationComplete && !_activeEditing!.ConfirmDeclaration(line))
                    {
                        return null;
                    }
                }
                else if (site.Kind != CompletionSiteKind.MemberHead || !site.NextIsDoubleColon)
                {
                    var comment = query.View.InBlockComment;
                    var normalized = CilLexer.StripComments(line, ref comment).Trim();
                    var (_, text) = InstructionParser.SplitLabels(normalized);
                    instruction = SymbolBinder.BindInstruction(CilSyntaxParser.ParseInstruction(text), scope);
                    if (site.Owner == "newarr" && instruction.Operand.Type is { } element
                        && !MemberEligibility.Admits(element, site, query.View))
                    {
                        return null;
                    }

                    if (site.Owner == "jmp" && instruction.Operand.Method is { } target
                        && !MemberEligibility.Admits(target.Method, site, query.View))
                    {
                        return null;
                    }

                    if (!Matches(instruction, candidate, site))
                    {
                        return null;
                    }
                }
            }

            var full = candidate.Method is { } method ? query.Members.FullSignature(method)
                : candidate.Field is { } field ? query.Members.FullSignature(field)
                : candidate.Type is { } type ? query.Types.Spell(type) : candidate.Rank.Label;
            var constraints = ConstraintDetails(candidate.Method?.GenericParameters
                ?? (candidate.Type is { } constrained ? scope.GenericParameterDeclarations(constrained) : []));
            if (constraints.Length > 0)
            {
                full += "\n" + constraints;
            }
            if (candidate.GenericOwner is { } argumentOwner)
            {
                full += "\ntype argument " + ArgumentHint(argumentOwner, site.ArgumentIndex);
            }
            var owner = candidate.Method?.DeclaringType ?? candidate.Field?.DeclaringType;
            var description = owner is not null ? SymbolRenderer.IlPath(owner)
                : candidate.Type is { } declared ? declared.AssemblyName : "";
            var detail = instruction is not null ? StackTransitionText.Format(instruction, query.View)
                : candidate.Type is { } kind ? TypeDetail(kind) : "";
            var label = candidate.Method is { } named ? MethodLabel(named) : candidate.Rank.Label;
            if (candidate.Method is { DeclaringType: { } declaring } member && query.AmbiguousNames.Contains(member.Name))
            {
                label = SymbolRenderer.IlPath(declaring) + "::" + label;
            }
            if (candidate.Type is not null && candidate.Slot < 0)
            {
                label = insertion.EndsWith("::", StringComparison.Ordinal) ? insertion[..^2] : insertion;
            }

            return new CompletionItem(label, detail, description, false)
            {
                Insert = insertion, Kind = candidate.Kind, Continues = continues,
                FullDetail = full + (description.Length == 0 ? "" : "\n" + description),
                Owner = candidate.GenericOwner?.Label,
            };
        }
        catch (Exception exception) when (ReplRecovery.IsRecoverable(exception))
        {
            // A failed candidate leaves its healthy neighbors available and the query position advances.
            return null;
        }
    }

    private static string? Insertion(CompletionQuery query, OperandCandidate candidate)
    {
        var site = query.Identity.Site;
        var scope = ((SnapshotBindingScope)query.View.Scope).ForConfirmation();
        if (candidate.Slot >= 0)
        {
            var local = site.Kind == CompletionSiteKind.Local;
            var variable = (local ? scope.Locals : scope.Arguments)[candidate.Slot];
            var text = variable.Name is { } name ? TypeNameFormatter.IlAsmIdentifier(name) : SymbolRenderer.Number(candidate.Slot);
            var index = local ? SymbolBinder.BindLocal(text, scope) : SymbolBinder.BindArgument(text, scope);
            return index == candidate.Slot ? text : null;
        }

        if (candidate.Method is { } method)
        {
            if (site.Owner is ".get" or ".set" or ".other" or ".addon" or ".removeon" or ".fire" or ".override with")
            {
                var owner = site.Owner == ".override with" ? query.Types.Spell(method.DeclaringType!) + "::" : "";
                return (method.IsStatic ? "" : "instance ") + query.Types.Spell(method.ReturnType) + " " + owner
                    + TypeNameFormatter.IlAsmIdentifier(method.Name) + "("
                    + string.Join(", ", method.Parameters.Select(parameter => query.Types.Spell(parameter.Type))) + ")";
            }

            if (site.Owner == ".override" && query.View.OpenMethod is { } implementation)
            {
                var target = query.Types.Spell(method.DeclaringType!) + "::" + TypeNameFormatter.IlAsmIdentifier(method.Name);
                return MemberSpeller.SameMethod(OverrideBinding.InBody(target, scope, implementation).Target.Method, method)
                    ? target : null;
            }

            var reference = query.Members.TrySpell(method, site);
            if (reference is not null && site.Kind == CompletionSiteKind.Signature)
            {
                var syntax = CilSyntaxParser.ParseMethodReference(reference);
                return reference[syntax.ParametersStart..];
            }

            return reference;
        }

        if (candidate.Field is { } field)
        {
            return query.Members.TrySpell(field, site);
        }

        if (candidate.Type is { } type)
        {
            var text = query.Types.TrySpell(type);
            return text is not null && site.Kind == CompletionSiteKind.MemberHead && !site.NextIsDoubleColon ? text + "::" : text;
        }

        return candidate.Rank.Name;
    }

    private CompletionItem? GenericStarter(CompletionQuery query, OperandCandidate candidate)
    {
        string? text;
        GenericCompletionTarget target;
        if (candidate.Type is { } type)
        {
            text = query.Types.GenericStarter(type);
            target = new GenericCompletionTarget
            {
                Type = type, Parameters = query.View.Scope.GenericParameterDeclarations(type), Label = SymbolRenderer.Pretty(type),
            };
        }
        else
        {
            var method = candidate.Method!;
            var reference = query.Members.TrySpell(method, query.Identity.Site);
            text = reference is null ? null : reference[..CilSyntaxParser.ParseMethodReference(reference).NameEnd] + "<";
            target = GenericCompletionBinding.ForMethod(method);
        }

        if (text is null)
        {
            return null;
        }

        var token = Guid.NewGuid().ToString("N");
        var paths = query.View.Snapshot.Types.Entries.Where(entry => entry.Type.Definition.Assembly < 0)
            .GroupBy(entry => entry.Type.Definition).ToDictionary(group => group.Key, group => group.First().FullName);
        _continuations.Add(token, new CompletionContinuation(target, text, query.Identity.Revision, query.Identity.BindingEpoch,
            query.View.DeclarationContext, paths));
        var constraints = ConstraintDetails(target.Parameters);
        var full = target.Method is { } signature ? query.Members.FullSignature(signature) : query.Types.Spell(target.Type!);
        return new CompletionItem(target.Label, "type arguments", candidate.Rank.DeclaringPath, false)
        {
            Insert = query.Identity.Site.NextIsAngle ? text[..^1] : text,
            CaretOffset = query.Identity.Site.NextIsAngle ? text.Length : null,
            Kind = CompletionKind.TypeArguments, Continues = true, Continuation = token,
            FullDetail = full + (constraints.Length == 0 ? "" : "\n" + constraints), Owner = candidate.GenericOwner?.Label,
        };
    }

    private static bool Matches(BoundInstruction instruction, OperandCandidate candidate, CompletionSite site)
    {
        if (candidate.Method is { } method)
        {
            return instruction.Operand.Method is { } actual && MemberSpeller.SameMethod(actual.Method, method);
        }

        if (candidate.Field is { } field)
        {
            return SymbolIdentity.Equal(instruction.Operand.Field, field);
        }

        if (candidate.Slot >= 0)
        {
            return (site.Kind == CompletionSiteKind.Local ? instruction.LocalIndex : instruction.ArgumentIndex) == candidate.Slot;
        }

        if (candidate.Type is null)
        {
            return true;
        }

        var found = false;
        if (instruction.Operand.Type is { } operand)
        {
            SymbolRelations.Rewrite(operand, part =>
            {
                found |= SymbolIdentity.Equal(part, candidate.Type);
                return null;
            });
        }

        return found;
    }

    private static string MethodLabel(MethodSymbol method) => method.Name
        + (method.Arity == 0 ? "" : "<" + string.Join(", ", method.GenericParameters.Select(parameter => parameter.Name)) + ">")
        + "(" + string.Join(", ", method.Parameters.Select(parameter => SymbolRenderer.Pretty(parameter.Type))) + ")";

    private static string TypeDetail(TypeSymbol type) => type.IsGenericParameter ? "generic parameter"
        : type.IsInterface ? "interface" : type.IsValueTypeShape ? "value type" : "class";

    private static string ConstraintDetails(IReadOnlyList<GenericParameterSymbol> parameters) =>
        string.Join("\n", parameters.Select(parameter => parameter.Name + ": " + parameter.Attributes
            + (parameter.Constraints.Count == 0 ? "" : ", "
                + string.Join(", ", parameter.Constraints.Select(SymbolRenderer.IlPath)))));
}
