namespace IlRepl.Protocol;

/// <summary>
/// Walks one line's syntax to locate the completion range and semantic site under its caret.
/// </summary>
internal sealed class CaretWalk
{
    private static readonly string[] Conventions =
        ["instance", "explicit", "vararg", "unmanaged", "cdecl", "stdcall", "thiscall", "fastcall", "default", "winapi", "platformapi"];
    private static readonly string[] FieldModifiers =
        ["public", "private", "family", "assembly", "famandassem", "famorassem", "privatescope", "static", "initonly", "literal",
            "specialname", "rtspecialname", "notserialized"];
    private static readonly string[] PropertyModifiers = ["specialname", "rtspecialname", "instance", "default"];
    private static readonly string[] MethodModifiers =
        ["public", "private", "family", "assembly", "famandassem", "famorassem", "privatescope", "static", "instance", "virtual",
            "newslot", "final", "abstract", "hidebysig", "specialname", "rtspecialname", "strict", "vararg", "default", "explicit",
            "pinvokeimpl", "unmanagedexp", "reqsecobj", "flags"];

    private readonly CilTokenizer _tokenizer;
    private readonly CilLineReader _r;
    private readonly string _line;
    private readonly int _caret;

    /// <summary>
    /// Initializes a classification over the line's lexemes.
    /// </summary>
    /// <param name="tokenizer">The shared IL vocabulary.</param>
    /// <param name="reader">The line's grammar reader.</param>
    /// <param name="line">The original line.</param>
    /// <param name="caret">The caret offset.</param>
    public CaretWalk(CilTokenizer tokenizer, CilLineReader reader, string line, int caret)
    {
        _tokenizer = tokenizer;
        _r = reader;
        _line = line;
        _caret = caret;
    }

    /// <summary>
    /// Classifies the caret's operand site within the line.
    /// </summary>
    /// <returns>The completion site, or null when the first-word catalog owns the caret.</returns>
    public CompletionSite? Line()
    {
        var i = 0;
        while (_r.IsName(i) && _r.IsPunct(i + 1, ':'))
        {
            i += 2;
        }

        if (i >= _r.Count)
        {
            return null;
        }

        if (_caret <= _r.EndOf(i))
        {
            // The first word is the local catalog's.
            return null;
        }

        if (_r.KindAt(i) == CilLexemeKind.Word)
        {
            var word = _r.TextAt(i);
            if (_tokenizer.IsOpcode(word))
            {
                return Instruction(i);
            }

            if (word[0] == '.')
            {
                return _tokenizer.IsDirective(word) ? Directive(i) : Command(i);
            }

            if (word.SequenceEqual("catch"))
            {
                return TypeAfter(i + 1, _r.Count, "catch", complete: true) ?? CompletionSite.None;
            }

            return null;
        }

        if ((_r.IsPunct(i, '}') || _r.IsPunct(i, '{')) && _r.IsWord(i + 1, "catch"))
        {
            return _caret <= _r.EndOf(i + 1) ? null : TypeAfter(i + 2, _r.Count, "catch", complete: true) ?? CompletionSite.None;
        }

        return null;
    }

    private CompletionSite? Instruction(int head)
    {
        var word = _r.TextAt(head).ToString();
        var kind = _tokenizer.OperandKindOf(word);
        var i = head + 1;
        var site = Operand(i, kind, word, out var next);
        if (site is not null)
        {
            return site;
        }

        if (word.EndsWith('.') && _r.IsOpcode(next) && _caret > _r.EndOf(next))
        {
            // A same-line prefix, constrained. or tail., followed by the instruction it prefixes.
            return Instruction(next);
        }

        return word.EndsWith('.') && _r.IsOpcode(next) ? null : site;
    }

    private CompletionSite? Operand(int i, CilOperandKind kind, string owner, out int next)
    {
        next = i;
        switch (kind)
        {
            case CilOperandKind.Branch:
                next = _r.IsName(i) ? i + 1 : i;
                return WordSite(i, CompletionSiteKind.Label, owner, -1);
            case CilOperandKind.Switch:
                {
                    if (!_r.IsPunct(i, '('))
                    {
                        return _caret >= _r.EndOf(i - 1) ? Slot(CompletionSiteKind.Label, owner, 0, true) : null;
                    }

                    var close = _r.Matching(i);
                    next = close < 0 ? _r.Count : close + 1;
                    if (!Inside(i, close))
                    {
                        return null;
                    }

                    var index = 0;
                    for (var j = i + 1; j < (close < 0 ? _r.Count : close); j++)
                    {
                        if (_r.IsPunct(j, ','))
                        {
                            index++;
                            continue;
                        }

                        if (_r.IsName(j) && On(j))
                        {
                            return Site(CompletionSiteKind.Label, owner, _r.StartOf(j), _r.StartOf(j), _r.EndOf(j)) with
                            { ArgumentIndex = index };
                        }
                    }

                    return Slot(CompletionSiteKind.Label, owner, index, true);
                }

            case CilOperandKind.Variable:
                {
                    var isArgument = owner.StartsWith("ldarg", StringComparison.Ordinal)
                        || owner.StartsWith("starg", StringComparison.Ordinal);
                    next = _r.IsName(i) || _r.KindAt(i) == CilLexemeKind.Number ? i + 1 : i;
                    return WordSite(i, isArgument ? CompletionSiteKind.Argument : CompletionSiteKind.Local, owner, -1);
                }

            case CilOperandKind.Type:
                next = _r.ReadType(i);
                return TypeAfter(i, next, owner, complete: true);
            case CilOperandKind.Member:
            case CilOperandKind.Field:
                next = _r.ReadMemberReference(i);
                return Member(i, owner, kind == CilOperandKind.Field ? CompletionSiteKind.Field
                : owner == "newobj" ? CompletionSiteKind.Constructor : CompletionSiteKind.Method, complete: true);
            case CilOperandKind.Token:
                if (_r.IsWord(i, "method") || _r.IsWord(i, "field"))
                {
                    if (_caret <= _r.EndOf(i))
                    {
                        return CompletionSite.None;
                    }

                    var isField = _r.IsWord(i, "field");
                    next = _r.ReadMemberReference(i + 1);
                    return Member(i + 1, owner + (isField ? " field" : " method"),
                    isField ? CompletionSiteKind.Field : CompletionSiteKind.Method, complete: true);
                }

                next = _r.ReadType(i);
                return TypeAfter(i, next, owner, complete: true);
            case CilOperandKind.Signature:
                {
                    var j = i;
                    while (_r.KindAt(j) == CilLexemeKind.Word && IsOneOf(j, Conventions))
                    {
                        if (On(j))
                        {
                            return CompletionSite.None;
                        }

                        j++;
                    }

                    var returnEnd = _r.ReadType(j);
                    var paren = _r.IsPunct(returnEnd, '(') ? returnEnd : -1;
                    next = paren < 0 ? returnEnd : _r.Matching(paren) is var pc && pc >= 0 ? pc + 1 : _r.Count;
                    if (paren >= 0 && Inside(paren, _r.Matching(paren)))
                    {
                        return Parameters(paren, _r.Matching(paren), owner, true);
                    }

                    return TypeAfter(j, returnEnd, owner, complete: true);
                }

            default:
                if (owner.EndsWith('.'))
                {
                    // A prefix opcode: its number, if it takes one, then the instruction it prefixes.
                    next = _r.KindAt(i) == CilLexemeKind.Number ? i + 1 : i;
                    return null;
                }

                return _caret > _r.EndOf(i - 1) ? CompletionSite.None : null;
        }
    }

    private CompletionSite? Directive(int head)
    {
        var word = _r.TextAt(head).ToString();
        var i = head + 1;
        switch (word)
        {
            case ".locals":
                if (_r.IsWord(i, "init"))
                {
                    if (_caret <= _r.EndOf(i))
                    {
                        return CompletionSite.None;
                    }

                    i++;
                }

                return List(i, word);
            case ".args":
            case ".typeargs":
                return List(i, word);
            case ".method":
                return MethodHeader(i, word);
            case ".class":
                return ClassHeader(i, word);
            case ".field":
            case ".event":
            case ".property":
                return FieldLike(i, word);
            case ".get":
            case ".set":
            case ".other":
            case ".addon":
            case ".removeon":
            case ".fire":
                return Member(i, word, CompletionSiteKind.Method, complete: true) ?? CompletionSite.None;
            case ".override":
                {
                    if (_r.IsWord(i, "method"))
                    {
                        if (_caret <= _r.EndOf(i))
                        {
                            return CompletionSite.None;
                        }

                        i++;
                    }

                    var end = _r.ReadMemberReference(i);
                    if (_caret <= _r.EndOf(end - 1) || !_r.IsWord(end, "with"))
                    {
                        return Member(i, word, CompletionSiteKind.Method, complete: true) ?? CompletionSite.None;
                    }

                    var with = end + 1;
                    if (_caret <= _r.EndOf(end))
                    {
                        return CompletionSite.None;
                    }

                    if (_r.IsWord(with, "method"))
                    {
                        if (_caret <= _r.EndOf(with))
                        {
                            return CompletionSite.None;
                        }

                        with++;
                    }

                    return Member(with, ".override with", CompletionSiteKind.Method, complete: true) ?? CompletionSite.None;
                }

            case ".custom":
                {
                    var end = _r.ReadMemberReference(i);
                    if (_r.IsPunct(end, '=') && _caret >= _r.StartOf(end))
                    {
                        return CompletionSite.None;
                    }

                    return Member(i, word, CompletionSiteKind.Constructor, complete: true) ?? CompletionSite.None;
                }

            default:
                return CompletionSite.None;
        }
    }

    private CompletionSite? Command(int head)
    {
        var word = _r.TextAt(head);
        if (word.SequenceEqual(".dis") || word.SequenceEqual(".disassemble"))
        {
            return Member(head + 1, ".dis", CompletionSiteKind.Method, complete: true) ?? CompletionSite.None;
        }

        return CompletionSite.None;
    }

    private CompletionSite? List(int i, string owner)
    {
        if (!_r.IsPunct(i, '('))
        {
            return _caret >= _r.EndOf(i - 1) ? Slot(CompletionSiteKind.Type, owner, 0, false) : CompletionSite.None;
        }

        var close = _r.Matching(i);
        var complete = close >= 0 && ItemsHaveTypes(i, close);
        if (!Inside(i, close))
        {
            return CompletionSite.None;
        }

        return Parameters(i, close, owner, complete) ?? CompletionSite.None;
    }

    private bool ItemsHaveTypes(int open, int close)
    {
        var a = open + 1;
        while (a < close)
        {
            if (_r.IsPunct(a, ','))
            {
                a++;
                continue;
            }

            var start = SkipItemPrefix(a);
            var typeEnd = _r.ReadType(start);
            if (typeEnd == start)
            {
                return false;
            }

            a = typeEnd;
            while (a < close && !_r.IsPunct(a, ','))
            {
                a++;
            }
        }

        return true;
    }

    private int SkipItemPrefix(int a)
    {
        if (_r.IsPunct(a, '[') && _r.KindAt(a + 1) == CilLexemeKind.Number && _r.IsPunct(a + 2, ']'))
        {
            a += 3;
        }

        while (_r.KindAt(a) == CilLexemeKind.AssemblyHint
            && (_r.TextAt(a).SequenceEqual("[in]") || _r.TextAt(a).SequenceEqual("[out]") || _r.TextAt(a).SequenceEqual("[opt]")))
        {
            a++;
        }

        return a;
    }

    private CompletionSite? MethodHeader(int i, string owner)
    {
        var afterModifiers = i;
        while (_r.KindAt(afterModifiers) == CilLexemeKind.Word && IsOneOf(afterModifiers, MethodModifiers))
        {
            afterModifiers++;
            if (_r.IsPunct(afterModifiers, '('))
            {
                var close = _r.Matching(afterModifiers);
                if (close < 0)
                {
                    return CompletionSite.None;
                }

                afterModifiers = close + 1;
            }
        }

        var typeEnd = _r.ReadType(afterModifiers);
        var hasType = typeEnd > afterModifiers;
        var nameIndex = hasType && _r.IsName(typeEnd) ? typeEnd : -1;
        var afterName = nameIndex >= 0 ? nameIndex + 1 : typeEnd;
        var genericOpen = _r.IsPunct(afterName, '<') ? afterName : -1;
        var genericClose = genericOpen >= 0 ? _r.Matching(genericOpen) : -1;
        var afterGeneric = genericOpen < 0 ? afterName : genericClose < 0 ? _r.Count : genericClose + 1;
        var paren = _r.IsPunct(afterGeneric, '(') ? afterGeneric : -1;
        var parenClose = paren >= 0 ? _r.Matching(paren) : -1;
        var complete = hasType && nameIndex >= 0 && paren >= 0 && parenClose >= 0 && !HasOpenList(i, _r.Count)
            && HeaderEnd(_r.ReadModifiers(parenClose + 1, stopAtType: false));

        if (_caret <= _r.EndOf(afterModifiers - 1) && afterModifiers > i)
        {
            return CompletionSite.None;
        }

        if (!hasType)
        {
            return _caret >= _r.EndOf(afterModifiers - 1) && (afterModifiers >= _r.Count || _caret <= _r.StartOf(afterModifiers))
                ? Slot(CompletionSiteKind.Type, owner, -1, complete) : CompletionSite.None;
        }

        if (TypeAfter(afterModifiers, typeEnd, owner, complete) is { } returnSite)
        {
            return returnSite;
        }

        if (genericOpen >= 0 && Inside(genericOpen, genericClose))
        {
            return GenericParameters(genericOpen, genericClose, owner, complete);
        }

        if (paren >= 0 && Inside(paren, parenClose))
        {
            return Parameters(paren, parenClose, owner, complete) ?? CompletionSite.None;
        }

        return CompletionSite.None;
    }

    private CompletionSite? ClassHeader(int i, string owner)
    {
        var afterModifiers = _r.ReadModifiers(i, stopAtType: false);
        var nameIndex = _r.IsName(afterModifiers) ? afterModifiers : -1;
        var j = nameIndex >= 0 ? nameIndex + 1 : afterModifiers;
        var complete = nameIndex >= 0 && !HasOpenList(i, _r.Count);
        CompletionSite? selected = null;
        if (_r.IsPunct(j, '<'))
        {
            var close = _r.Matching(j);
            if (Inside(j, close))
            {
                selected = GenericParameters(j, close, owner, complete);
            }

            j = close < 0 ? _r.Count : close + 1;
        }

        if (_r.IsWord(j, "extends"))
        {
            var start = j + 1;
            var end = _r.IsWord(start, "implements") ? start : _r.ReadType(start);
            complete &= InheritanceTypeComplete(start, end);
            if (_caret > _r.EndOf(j) && (_caret <= _r.EndOf(end - 1) || end == start && !_r.IsWord(end, "implements")))
            {
                selected = TypeAfter(start, end, "extends", complete);
            }

            j = end;
        }

        if (_r.IsWord(j, "implements"))
        {
            var clauseEnd = _r.EndOf(j);
            var start = j + 1;
            var index = 0;
            while (true)
            {
                var end = _r.ReadType(start);
                complete &= InheritanceTypeComplete(start, end);
                if (_caret > clauseEnd && selected is null)
                {
                    if (_caret < _r.StartOf(start))
                    {
                        selected = Slot(CompletionSiteKind.Type, "implements", index, complete);
                    }
                    else if (TypeAfter(start, end, "implements", complete) is { } site)
                    {
                        selected = site with { ArgumentIndex = site.Kind == CompletionSiteKind.Type ? index : site.ArgumentIndex };
                    }
                }

                j = end;
                if (!_r.IsPunct(j, ','))
                {
                    break;
                }

                start = j + 1;
                index++;
            }
        }

        // A later clause can still be unfinished when the caret returns to an earlier type.
        complete &= HeaderEnd(j);
        return selected is not null ? selected with { DeclarationComplete = complete } : CompletionSite.None;
    }

    private bool InheritanceTypeComplete(int start, int end)
    {
        if (_r.IsWord(start, "class") || _r.IsWord(start, "valuetype"))
        {
            start++;
        }

        if (_r.KindAt(start) == CilLexemeKind.AssemblyHint)
        {
            start++;
        }

        if (start >= end || !_r.IsName(start) && _r.KindAt(start) != CilLexemeKind.GenericParameter)
        {
            return false;
        }

        for (var i = start + 1; i < end; i++)
        {
            if (!_r.IsPunct(i, '<'))
            {
                continue;
            }

            var close = _r.Matching(i);
            var argument = i + 1;
            if (close < 0 || argument == close)
            {
                return false;
            }

            while (argument < close)
            {
                var next = _r.ReadType(argument);
                if (next > close || !InheritanceTypeComplete(argument, next))
                {
                    return false;
                }

                if (next == close)
                {
                    break;
                }

                if (!_r.IsPunct(next, ',') || next + 1 == close)
                {
                    return false;
                }

                argument = next + 1;
            }

            i = close;
        }

        return true;
    }

    private CompletionSite? FieldLike(int i, string owner)
    {
        var afterModifiers = i;
        if (owner == ".field" && _r.IsPunct(i, '[') && _r.KindAt(i + 1) == CilLexemeKind.Number && _r.IsPunct(i + 2, ']'))
        {
            afterModifiers += 3;
        }

        while (_r.KindAt(afterModifiers) == CilLexemeKind.Word
            && IsOneOf(afterModifiers, owner == ".field" ? FieldModifiers : PropertyModifiers))
        {
            afterModifiers++;
        }

        if (_caret <= _r.EndOf(afterModifiers - 1) && afterModifiers > i)
        {
            return CompletionSite.None;
        }

        var typeEnd = _r.ReadType(afterModifiers);
        var hasType = typeEnd > afterModifiers;
        var nameIndex = hasType && _r.IsName(typeEnd) ? typeEnd : -1;
        var paren = nameIndex >= 0 && _r.IsPunct(nameIndex + 1, '(') ? nameIndex + 1 : -1;
        var parenClose = paren >= 0 ? _r.Matching(paren) : -1;
        var complete = hasType && nameIndex >= 0 && !HasOpenList(i, _r.Count) && (paren < 0 || parenClose >= 0)
            && (owner == ".field" ? FieldInitializerComplete(nameIndex + 1) : HeaderEnd(paren < 0 ? nameIndex + 1 : parenClose + 1));
        if (!hasType)
        {
            return _caret >= _r.EndOf(afterModifiers - 1) && (afterModifiers >= _r.Count || _caret <= _r.StartOf(afterModifiers))
                ? Slot(CompletionSiteKind.Type, owner, -1, complete) : CompletionSite.None;
        }

        if (TypeAfter(afterModifiers, typeEnd, owner, complete) is { } typeSite)
        {
            return typeSite;
        }

        if (owner == ".property" && paren >= 0 && Inside(paren, parenClose))
        {
            return Parameters(paren, parenClose, owner, complete) ?? CompletionSite.None;
        }

        return CompletionSite.None;
    }

    private bool HeaderEnd(int i) => i >= _r.Count || _r.IsPunct(i, '{')
        && (i + 1 >= _r.Count || _r.IsPunct(i + 1, '}') && i + 2 >= _r.Count);

    private bool FieldInitializerComplete(int i)
    {
        if (!_r.IsPunct(i, '='))
        {
            return i >= _r.Count;
        }

        i++;
        if (_r.IsPunct(i + 1, '('))
        {
            var close = _r.Matching(i + 1);
            return close >= 0 && close + 1 == _r.Count && (close > i + 2 || _r.IsWord(i, "bytearray"));
        }

        if (_r.IsPrimitive(i) || _r.IsWord(i, "bytearray"))
        {
            return false;
        }

        if (_r.KindAt(i) is CilLexemeKind.String or CilLexemeKind.Quoted)
        {
            var text = _r.TextAt(i);
            var last = text.Length - 1;
            if (last < 1 || text[last] != text[0])
            {
                return false;
            }

            var before = last - 1;
            while (before > 0 && text[before] == '\\')
            {
                before--;
            }

            if ((last - before) % 2 == 0)
            {
                return false;
            }
        }

        return _r.ReadLiteral(i) == _r.Count && i < _r.Count;
    }

    private CompletionSite GenericParameters(int open, int close, string owner, bool complete)
    {
        var end = close < 0 ? _r.Count : close;
        for (var j = open + 1; j < end; j++)
        {
            if (_r.IsPunct(j, '('))
            {
                var constraintsClose = _r.Matching(j);
                if (Inside(j, constraintsClose))
                {
                    return Parameters(j, constraintsClose, owner, complete) ?? CompletionSite.None;
                }

                j = constraintsClose < 0 ? end : constraintsClose;
            }
        }

        return CompletionSite.None;
    }

    /// <summary>
    /// The caret's type site within a parenthesized list of declarations and optional initializers.
    /// </summary>
    private CompletionSite? Parameters(int open, int close, string owner, bool complete)
    {
        var end = close < 0 ? _r.Count : close;
        var a = open + 1;
        var index = 0;
        if (_caret < _r.EndOf(open))
        {
            return null;
        }

        while (a < end)
        {
            if (_r.IsPunct(a, ','))
            {
                if (_caret <= _r.StartOf(a))
                {
                    return CompletionSite.None;
                }

                a++;
                index++;
                continue;
            }

            if (_r.KindAt(a) == CilLexemeKind.Ellipsis)
            {
                if (On(a))
                {
                    return CompletionSite.None;
                }

                a++;
                continue;
            }

            if (_caret < _r.StartOf(a))
            {
                return Slot(CompletionSiteKind.Type, owner, index, complete);
            }

            var start = SkipItemPrefix(a);
            if (_caret <= _r.EndOf(start - 1) && start > a)
            {
                return CompletionSite.None;
            }

            var typeEnd = _r.ReadType(start);
            if (typeEnd == start)
            {
                // Not a type: a name alone, a literal, or something the grammar has no shape for.
                if (On(start))
                {
                    return Site(CompletionSiteKind.Type, owner, _r.StartOf(start), _r.StartOf(start), _r.EndOf(start)) with
                    { ArgumentIndex = index, DeclarationComplete = complete };
                }

                a = start + 1;
                continue;
            }

            if (TypeAfter(start, typeEnd, owner, complete) is { } site)
            {
                return site with
                {
                    ArgumentIndex = site.Kind == CompletionSiteKind.Type ? index : site.ArgumentIndex,
                    EnclosingParameterIndex = index,
                };
            }

            a = typeEnd;
            if (_r.IsName(a) && (a + 1 >= _r.Count || _r.IsPunct(a + 1, ',') || _r.IsPunct(a + 1, ')') || _r.IsPunct(a + 1, '=')))
            {
                if (On(a))
                {
                    return CompletionSite.None;
                }

                a++;
            }

            if (_r.IsPunct(a, '='))
            {
                var literalEnd = _r.ReadLiteral(a + 1);
                if (_caret >= _r.StartOf(a) && (literalEnd > a + 1 ? _caret <= _r.EndOf(literalEnd - 1) : true)
                && (a + 1 >= end || _caret <= _r.StartOf(a + 1) || literalEnd > a + 1))
                {
                    return CompletionSite.None;
                }

                a = Math.Max(a + 1, literalEnd);
            }

            if (a < end && !_r.IsPunct(a, ','))
            {
                if (_caret <= _r.EndOf(a) && _caret >= _r.StartOf(a))
                {
                    return CompletionSite.None;
                }

                a++;
            }
        }

        if (close < 0 || _caret <= _r.StartOf(close))
        {
            return Slot(CompletionSiteKind.Type, owner, index, complete);
        }

        return null;
    }

    /// <summary>
    /// The expected type site, an empty type slot, or null when the caret is elsewhere.
    /// </summary>
    private CompletionSite? TypeAfter(int i, int end, string owner, bool complete)
    {
        if (end == i)
        {
            var previousEnd = _r.EndOf(i - 1);
            return _caret >= previousEnd && (i >= _r.Count || _caret <= _r.StartOf(i))
                ? Slot(CompletionSiteKind.Type, owner, -1, complete) : null;
        }

        if (_caret < _r.StartOf(i) || _caret > _r.EndOf(end - 1))
        {
            return null;
        }

        return Type(i, end, owner, complete);
    }

    /// <summary>
    /// The name, argument or modifier site inside a type, excluding keywords and suffixes.
    /// </summary>
    private CompletionSite? Type(int i, int end, string owner, bool complete)
        => TypeCore(i, end, owner, complete) is { } site
            ? site with { EnclosingTypeStart = _r.StartOf(i), EnclosingParameterIndex = -1 } : null;

    private CompletionSite? TypeCore(int i, int end, string owner, bool complete)
    {
        var j = i;
        while (_r.IsWord(j, "class") || _r.IsWord(j, "valuetype"))
        {
            if (On(j) && _caret < _r.EndOf(j))
            {
                return CompletionSite.None;
            }

            j++;
        }

        var hint = -1;
        if (_r.KindAt(j) == CilLexemeKind.AssemblyHint)
        {
            if (_r.StartOf(j) < _caret && _caret < _r.EndOf(j))
            {
                return CompletionSite.None;
            }

            hint = j;
            j++;
        }

        if (_r.IsWord(j, "method"))
        {
            if (On(j))
            {
                return CompletionSite.None;
            }

            var k = j + 1;
            while (_r.KindAt(k) == CilLexemeKind.Word && IsOneOf(k, Conventions))
            {
                if (On(k))
                {
                    return CompletionSite.None;
                }

                k++;
            }

            var returnEnd = _r.ReadType(k);
            if (TypeAfter(k, returnEnd, owner, complete) is { } returnSite)
            {
                return returnSite with
                {
                    IsFunctionPointerReturn = returnSite.IsFunctionPointerReturn || returnSite.ArgumentIndex < 0,
                };
            }

            var star = _r.IsPunct(returnEnd, '*') ? returnEnd + 1 : returnEnd;
            if (_r.IsPunct(star, '('))
            {
                var close = _r.Matching(star);
                if (Inside(star, close))
                {
                    return Parameters(star, close, owner, complete) ?? CompletionSite.None;
                }
            }

            return CompletionSite.None;
        }

        var nameStart = j;
        var nameEnd = -1;
        var isParameter = false;
        if (_r.KindAt(j) == CilLexemeKind.Word)
        {
            while ((_r.IsWord(j, "native") || _r.IsWord(j, "unsigned"))
                && (_r.IsWord(j + 1, "native") || _r.IsWord(j + 1, "unsigned") || _r.IsPrimitive(j + 1)))
            {
                j++;
            }

            nameEnd = j;
            j++;
        }
        else if (_r.KindAt(j) is CilLexemeKind.Quoted or CilLexemeKind.GenericParameter)
        {
            isParameter = _r.KindAt(j) == CilLexemeKind.GenericParameter;
            nameEnd = j;
            j++;
        }

        if (nameEnd < 0)
        {
            // A hint or a keyword with no name after it yet.
            var rangeStart = hint >= 0 ? _r.StartOf(hint) : _caret;
            return _caret >= (hint >= 0 ? _r.EndOf(hint) : _r.EndOf(j - 1))
                ? Site(CompletionSiteKind.Type, owner, _caret, rangeStart, _caret) with { DeclarationComplete = complete }
                : CompletionSite.None;
        }

        var genericOpen = _r.IsPunct(j, '<') ? j : -1;
        var genericClose = genericOpen >= 0 ? _r.Matching(genericOpen) : -1;
        if (genericOpen >= 0 && Inside(genericOpen, genericClose))
        {
            var ownerStart = hint >= 0 ? _r.StartOf(hint) : _r.StartOf(nameStart);
            return Arguments(genericOpen, genericClose, _line[ownerStart.._r.EndOf(nameEnd)], owner, complete);
        }

        var afterName = genericOpen < 0 ? j : genericClose < 0 ? _r.Count : genericClose + 1;
        var closedList = genericOpen >= 0 && genericClose >= 0;
        var onName = _caret >= _r.StartOf(nameStart) && (_caret <= _r.EndOf(nameEnd) || closedList && _caret == _r.EndOf(genericClose));
        var afterHint = hint >= 0 && _caret == _r.EndOf(hint);
        if (onName || afterHint)
        {
            if (isParameter)
            {
                return Site(CompletionSiteKind.GenericParameter, owner, _r.StartOf(nameStart), _r.StartOf(nameStart), _r.EndOf(nameEnd))
                    with
                { DeclarationComplete = complete };
            }

            var rangeStart = hint >= 0 ? _r.StartOf(hint) : _r.StartOf(nameStart);
            var rangeEnd = closedList ? _r.EndOf(genericClose) : _r.EndOf(nameEnd);
            var after = closedList ? genericClose + 1 : nameEnd + 1;
            return Site(CompletionSiteKind.Type, owner, _r.StartOf(nameStart), rangeStart, rangeEnd) with
            {
                NextIsAngle = genericOpen >= 0 && genericClose < 0,
                GenericNameEnd = _r.EndOf(nameEnd),
                GenericOpenOffset = genericOpen >= 0 ? _r.StartOf(genericOpen) : -1,
                NextIsDoubleColon = _r.KindAt(after) == CilLexemeKind.DoubleColon,
                NextIsParen = _r.IsPunct(after, '('),
                DeclarationComplete = complete,
            };
        }

        // Suffixes: brackets, pointers, references, pinned, and modifiers.
        var s = afterName;
        while (s < end)
        {
            if (_r.IsPunct(s, '['))
            {
                var close = _r.Matching(s);
                if (close < 0)
                {
                    return CompletionSite.None;
                }

                s = close + 1;
                continue;
            }

            if (_r.IsWord(s, "modreq") || _r.IsWord(s, "modopt"))
            {
                if (_r.IsPunct(s + 1, '('))
                {
                    var close = _r.Matching(s + 1);
                    if (Inside(s + 1, close))
                    {
                        var innerEnd = _r.ReadType(s + 2);
                        return TypeAfter(s + 2, innerEnd, owner, complete) ?? CompletionSite.None;
                    }

                    s = close < 0 ? end : close + 1;
                    continue;
                }
            }

            s++;
        }

        return CompletionSite.None;
    }

    /// <summary>
    /// The type argument the caret is in, between angle brackets: the innermost wins.
    /// </summary>
    private CompletionSite Arguments(int open, int close, string ownerText, string owner, bool complete)
    {
        var end = close < 0 ? _r.Count : close;
        var a = open + 1;
        var index = 0;
        while (a < end)
        {
            if (_r.IsPunct(a, ','))
            {
                if (_caret <= _r.StartOf(a))
                {
                    return CompletionSite.None;
                }

                a++;
                index++;
                continue;
            }

            if (_caret < _r.StartOf(a))
            {
                return ArgumentSlot(owner, ownerText, index, complete);
            }

            var argumentEnd = _r.ReadType(a);
            if (argumentEnd == a)
            {
                argumentEnd = a + 1;
            }

            if (_caret <= _r.EndOf(argumentEnd - 1))
            {
                var site = Type(a, argumentEnd, owner, complete);
                if (site is null || site.Kind == CompletionSiteKind.None)
                {
                    return CompletionSite.None;
                }

                return site.Kind == CompletionSiteKind.Type
                    ? site with
                    {
                        Kind = CompletionSiteKind.TypeArgument, GenericOwnerText = ownerText,
                        ArgumentIndex = index, SuppliedArguments = index,
                    }
                    : site;
            }

            a = argumentEnd;
        }

        if (index > 0 || a == open + 1)
        {
            return ArgumentSlot(owner, ownerText, index, complete);
        }

        return CompletionSite.None;
    }

    private CompletionSite ArgumentSlot(string owner, string ownerText, int index, bool complete) =>
        Slot(CompletionSiteKind.TypeArgument, owner, index, complete) with { GenericOwnerText = ownerText, SuppliedArguments = index };

    /// <summary>
    /// The caret's site within a qualified member reference or a session method reference.
    /// </summary>
    private CompletionSite? Member(int i, string owner, CompletionSiteKind memberKind, bool complete)
    {
        var refEnd = _r.ReadMemberReference(i);
        var j = i;
        var explicitInstance = false;
        while (_r.KindAt(j) == CilLexemeKind.Word && IsOneOf(j, Conventions))
        {
            if (_r.TextAt(j).SequenceEqual("instance"))
            {
                explicitInstance = true;
            }

            if (On(j) && _caret < _r.EndOf(j))
            {
                return CompletionSite.None;
            }

            j++;
        }

        if (_caret < _r.EndOf(j - 1) && j > i)
        {
            return CompletionSite.None;
        }

        var separator = Separator(j, refEnd);
        if (separator < 0)
        {
            return Session(i, j, refEnd, owner, complete, explicitInstance);
        }

        var n = separator + 1;
        var hasName = _r.IsName(n);
        var genericOpen = hasName && _r.IsPunct(n + 1, '<') ? n + 1 : -1;
        var genericClose = genericOpen >= 0 ? _r.Matching(genericOpen) : -1;
        var afterGeneric = genericOpen < 0 ? (hasName ? n + 1 : n) : genericClose < 0 ? _r.Count : genericClose + 1;
        var paren = _r.IsPunct(afterGeneric, '(') ? afterGeneric : -1;
        var parenClose = paren >= 0 ? _r.Matching(paren) : -1;
        var referenceEnd = _r.EndOf(refEnd - 1);

        var typeComplete = complete && hasName && !HasOpenList(i, refEnd)
            && (parenClose >= 0 || owner == ".override" && paren < 0);

        var typeOne = _r.ReadType(j);
        var declaringStart = typeOne < separator && typeOne > j ? typeOne : j;
        var returnTypeText = declaringStart > j ? _line[_r.StartOf(j).._r.EndOf(declaringStart - 1)] : null;
        var declaringText = TypeText(declaringStart, separator);
        if (declaringStart > j && _caret <= _r.EndOf(declaringStart - 1))
        {
            return (Type(j, declaringStart, owner, typeComplete) ?? CompletionSite.None) with { ExplicitInstance = explicitInstance };
        }

        if (_caret <= _r.EndOf(separator - 1))
        {
            var site = Type(declaringStart, separator, owner, typeComplete);
            return site is { Kind: CompletionSiteKind.Type }
                ? site with
                {
                    Kind = CompletionSiteKind.MemberHead,
                    ExplicitInstance = explicitInstance,
                    ReturnTypeText = returnTypeText,
                    NextIsDoubleColon = true
                }
                : site ?? CompletionSite.None;
        }

        if (_caret < _r.EndOf(separator))
        {
            return CompletionSite.None;
        }

        if (hasName && On(n))
        {
            return Site(memberKind, owner, _r.StartOf(n), _r.StartOf(i), referenceEnd) with
            {
                DeclaringTypeText = declaringText,
                ReturnTypeText = returnTypeText,
                ExplicitInstance = explicitInstance,
                NextIsAngle = genericOpen >= 0,
                GenericNameEnd = hasName ? _r.EndOf(n) : -1,
                GenericOpenOffset = genericOpen >= 0 ? _r.StartOf(genericOpen) : -1,
                NextIsParen = paren >= 0,
                DeclarationComplete = complete,
            };
        }

        if (!hasName && _caret >= _r.EndOf(separator) && (n >= _r.Count || _caret <= _r.StartOf(n)))
        {
            return Site(memberKind, owner, _caret, _r.StartOf(i), _caret) with
            {
                DeclaringTypeText = declaringText,
                ReturnTypeText = returnTypeText,
                ExplicitInstance = explicitInstance,
                DeclarationComplete = complete,
            };
        }

        if (genericOpen >= 0 && Inside(genericOpen, genericClose))
        {
            return Arguments(genericOpen, genericClose, declaringText + "::" + _line[_r.StartOf(n).._r.EndOf(n)], owner, typeComplete);
        }

        if (genericOpen >= 0 && genericClose >= 0 && paren < 0 && _caret >= _r.EndOf(genericClose)
            && (genericClose + 1 >= _r.Count || _caret <= _r.StartOf(genericClose + 1)))
        {
            return Slot(CompletionSiteKind.Signature, owner, -1, complete) with
            {
                DeclaringTypeText = declaringText,
                ReturnTypeText = returnTypeText,
                ExplicitInstance = explicitInstance,
                GenericOwnerText = declaringText + "::" + _line[_r.StartOf(n).._r.EndOf(n)],
            };
        }

        if (paren >= 0 && Inside(paren, parenClose))
        {
            return (Parameters(paren, parenClose, owner, typeComplete) ?? CompletionSite.None) with
            { DeclaringTypeText = declaringText, ReturnTypeText = returnTypeText, ExplicitInstance = explicitInstance };
        }

        return CompletionSite.None;
    }

    /// <summary>
    /// A session method, accessor signature or incomplete declaring type without a double-colon separator.
    /// </summary>
    private CompletionSite? Session(int i, int j, int refEnd, string owner, bool complete, bool explicitInstance)
    {
        var returnEnd = _r.IsName(j) && !_r.IsPrimitive(j) && _r.IsBareMemberName(j) ? j : _r.ReadType(j);
        var bare = returnEnd < refEnd && _r.IsName(returnEnd) && _r.IsBareMemberName(returnEnd) ? returnEnd : -1;
        var returnText = returnEnd > j ? _line[_r.StartOf(j).._r.EndOf(returnEnd - 1)] : null;
        var genericOpen = bare >= 0 && _r.IsPunct(bare + 1, '<') ? bare + 1 : -1;
        var genericClose = genericOpen >= 0 ? _r.Matching(genericOpen) : -1;
        var afterGeneric = genericOpen < 0 ? bare + 1 : genericClose < 0 ? _r.Count : genericClose + 1;
        var paren = _r.IsPunct(afterGeneric, '(') ? afterGeneric : -1;
        var typeComplete = complete && bare >= 0 && paren >= 0 && _r.Matching(paren) >= 0 && !HasOpenList(i, refEnd);
        if (returnEnd > j && _caret > _r.EndOf(j - 1) && (_caret <= _r.EndOf(returnEnd - 1) || HasOpenList(j, returnEnd)))
        {
            // In the return type, or in the first type of a reference with no `::` yet.
            var site = Type(j, returnEnd, owner, typeComplete);
            if (bare < 0 && site is { Kind: CompletionSiteKind.Type })
            {
                return site with { Kind = CompletionSiteKind.MemberHead, ExplicitInstance = explicitInstance };
            }

            return (site ?? CompletionSite.None) with { ExplicitInstance = explicitInstance };
        }

        if (bare < 0)
        {
            // A return type, then the member head still being typed, or nothing yet.
            var head = _r.ReadType(returnEnd);
            if (head > returnEnd && _caret >= _r.StartOf(returnEnd) && (_caret <= _r.EndOf(head - 1) || HasOpenList(returnEnd, head)))
            {
                var site = Type(returnEnd, head, owner, typeComplete);
                return site is { Kind: CompletionSiteKind.Type }
                    ? site with { Kind = CompletionSiteKind.MemberHead, ExplicitInstance = explicitInstance, ReturnTypeText = returnText }
                    : (site ?? CompletionSite.None) with { ExplicitInstance = explicitInstance };
            }

            return head == returnEnd && _caret >= _r.EndOf(returnEnd - 1) && (returnEnd >= _r.Count || _caret <= _r.StartOf(returnEnd))
                ? Slot(CompletionSiteKind.MemberHead, owner, -1, complete) with
                { ExplicitInstance = explicitInstance, ReturnTypeText = returnText }
                : CompletionSite.None;
        }

        if (On(bare))
        {
            if (IsAccessor(owner))
            {
                return Site(CompletionSiteKind.Method, owner, _r.StartOf(bare), _r.StartOf(i), _r.EndOf(refEnd - 1)) with
                {
                    ReturnTypeText = returnText,
                    ExplicitInstance = explicitInstance,
                    NextIsAngle = genericOpen >= 0,
                    GenericNameEnd = _r.EndOf(bare),
                    GenericOpenOffset = genericOpen >= 0 ? _r.StartOf(genericOpen) : -1,
                    NextIsParen = paren >= 0,
                    DeclarationComplete = complete,
                };
            }

            return Site(CompletionSiteKind.MemberHead, owner, _r.StartOf(bare), _r.StartOf(bare), _r.EndOf(bare)) with
            {
                ReturnTypeText = returnText,
                ExplicitInstance = explicitInstance,
                NextIsAngle = genericOpen >= 0,
                GenericNameEnd = _r.EndOf(bare),
                GenericOpenOffset = genericOpen >= 0 ? _r.StartOf(genericOpen) : -1,
                NextIsParen = paren >= 0,
                DeclarationComplete = complete,
            };
        }

        if (genericOpen >= 0 && Inside(genericOpen, genericClose))
        {
            return Arguments(genericOpen, genericClose, _line[_r.StartOf(bare).._r.EndOf(bare)], owner, typeComplete);
        }

        if (paren >= 0 && Inside(paren, _r.Matching(paren)))
        {
            return (Parameters(paren, _r.Matching(paren), owner, typeComplete) ?? CompletionSite.None) with
            { ReturnTypeText = returnText, ExplicitInstance = explicitInstance };
        }

        return CompletionSite.None;
    }

    private static bool IsAccessor(string owner) => owner is ".get" or ".set" or ".other" or ".addon" or ".removeon" or ".fire";

    /// <summary>
    /// The type text without its class or valuetype keyword, for qualifier correction.
    /// </summary>
    private string TypeText(int from, int to)
    {
        while (from < to && (_r.IsWord(from, "class") || _r.IsWord(from, "valuetype")))
        {
            from++;
        }

        return from < to ? _line[_r.StartOf(from).._r.EndOf(to - 1)] : "";
    }

    /// <summary>
    /// Whether the type between the supplied indices has an unclosed bracket.
    /// </summary>
    private bool HasOpenList(int from, int to)
    {
        for (var k = from; k < to; k++)
        {
            if ((_r.IsPunct(k, '<') || _r.IsPunct(k, '(') || _r.IsPunct(k, '[')) && _r.Matching(k) < 0)
            {
                return true;
            }
        }

        return false;
    }

    private int Separator(int from, int to)
    {
        var depth = 0;
        for (var k = from; k < to && k < _r.Count; k++)
        {
            if (_r.KindAt(k) == CilLexemeKind.Punctuation)
            {
                var c = _line[_r.StartOf(k)];
                if (c is '<' or '(' or '[')
                {
                    depth++;
                }
                else if (c is '>' or ')' or ']')
                {
                    depth--;
                }
            }
            else if (_r.KindAt(k) == CilLexemeKind.DoubleColon && depth == 0)
            {
                return k;
            }
        }

        return -1;
    }

    private CompletionSite? WordSite(int i, CompletionSiteKind kind, string owner, int argumentIndex)
    {
        if ((_r.IsName(i) || _r.KindAt(i) == CilLexemeKind.Number) && On(i))
        {
            return Site(kind, owner, _r.StartOf(i), _r.StartOf(i), _r.EndOf(i)) with { ArgumentIndex = argumentIndex };
        }

        if (_caret >= _r.EndOf(i - 1) && (i >= _r.Count || _caret <= _r.StartOf(i)))
        {
            return Slot(kind, owner, argumentIndex, true);
        }

        return null;
    }

    private bool On(int i) => i >= 0 && i < _r.Count && _r.StartOf(i) <= _caret && _caret <= _r.EndOf(i);

    private bool Inside(int open, int close) => open >= 0 && _caret >= _r.EndOf(open) && (close < 0 || _caret <= _r.StartOf(close));

    private bool IsOneOf(int i, string[] words)
    {
        var text = _r.TextAt(i);
        foreach (var word in words)
        {
            if (text.SequenceEqual(word))
            {
                return true;
            }
        }

        return false;
    }

    private CompletionSite Site(CompletionSiteKind kind, string owner, int prefixStart, int rangeStart, int rangeEnd)
    {
        var prefix = prefixStart <= _caret ? _line[prefixStart.._caret] : "";
        return new CompletionSite(kind, owner, prefix, rangeStart, Math.Max(0, rangeEnd - rangeStart),
            _caret, null, null, false, -1, 0, null, false, false, false, true);
    }

    private CompletionSite Slot(CompletionSiteKind kind, string owner, int argumentIndex, bool complete) =>
        new(kind, owner, "", _caret, 0, _caret, null, null, false, argumentIndex, 0, null, false, false, false, complete);
}
