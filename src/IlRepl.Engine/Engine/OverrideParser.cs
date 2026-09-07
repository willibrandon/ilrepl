using System.Reflection;

namespace IlRepl.Engine;

/// <summary>
/// Parses <c>.override</c> lines. Inside a method body, <c>.override T::M</c> names the slot the
/// method implements, matched by the method's own signature, and <c>.override method callConv
/// Ret T::M(params)</c> names it in full. At class level, <c>.override T::M with method ...</c>
/// pairs a slot with an implementing method that may be declared later in the block.
/// </summary>
public static class OverrideParser
{
    /// <summary>
    /// Parses an <c>.override</c> written inside a method body.
    /// </summary>
    /// <param name="spec">The text after <c>.override</c>.</param>
    /// <param name="context">The parse context of the method.</param>
    /// <param name="method">The signature of the method being written.</param>
    /// <param name="source">The line as typed.</param>
    /// <returns>The override.</returns>
    /// <exception cref="ReplException">The target is not a virtual method the type can implement, or its signature differs.</exception>
    public static OverrideDeclaration ParseInBody(string spec, ParseContext context, MethodSignature method, string source)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(source);
        var s = spec.Trim();
        if (s.Contains(" with ", StringComparison.Ordinal))
        {
            throw new ReplException("the '.override X with Y' form belongs at class level; inside a method write .override T::M");
        }

        var target = ResolveTarget(s, context, method);
        Check(target, method);
        return new OverrideDeclaration(target.Method!, Describe(target), source);
    }

    /// <summary>
    /// Parses a class-level <c>.override T::M with method callConv Ret This::Name(params)</c>.
    /// </summary>
    /// <param name="spec">The text after <c>.override</c>.</param>
    /// <param name="context">The parse context of the open type.</param>
    /// <param name="source">The line as typed.</param>
    /// <returns>The override, with the implementing method named for resolution at close.</returns>
    /// <exception cref="ReplException">The line is malformed.</exception>
    public static ClassOverrideDeclaration ParseAtClassLevel(string spec, ParseContext context, string source)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(source);
        var s = spec.Trim();
        var with = s.IndexOf(" with ", StringComparison.Ordinal);
        if (with < 0)
        {
            throw new ReplException(".override T::M belongs inside the method that implements it (or use .override T::M with method ... at class level)");
        }

        var body = s[(with + 6)..].Trim();
        if (body.StartsWith("method ", StringComparison.Ordinal))
        {
            body = body[7..].Trim();
        }

        var bodyStatic = !body.StartsWith("instance ", StringComparison.Ordinal);
        if (!bodyStatic)
        {
            body = body[9..].Trim();
        }

        var separator = body.IndexOf("::", StringComparison.Ordinal);
        var paren = body.IndexOf('(', StringComparison.Ordinal);
        if (separator < 0 || paren < separator)
        {
            throw new ReplException("the 'with' side names a method of this type: with method instance int32 Point::Area()");
        }

        var returnText = body[..separator].Trim();
        var space = returnText.LastIndexOf(' ');
        if (space < 0)
        {
            throw new ReplException("the 'with' side needs a return type: with method instance int32 Point::Area()");
        }

        var returnType = TypeParser.Parse(returnText[..space], context);
        var name = InstructionParser.Unquote(body[(separator + 2)..paren].Trim());
        var close = TypeParser.FindMatchingParen(body, paren);
        var parameterTypes = TypeParser.SplitTopLevel(body[(paren + 1)..close]).Select(t => TypeParser.Parse(t, context)).ToList();
        var target = ResolveTarget(s[..with].Trim(), context, new MethodSignature(name, returnType, [.. parameterTypes.Select(t => new ArgumentDeclaration(t, null, null, ""))]) { Attributes = bodyStatic ? MethodAttributes.Static : MethodAttributes.PrivateScope });
        return new ClassOverrideDeclaration(target.Method!, Describe(target), name, returnType, parameterTypes, bodyStatic, source);
    }

    private static ResolvedMethod ResolveTarget(string text, ParseContext context, MethodSignature implementing)
    {
        var s = text.Trim();
        if (s.StartsWith("method ", StringComparison.Ordinal))
        {
            // The explicit form spells the target out; the resolver reads it as a call site would.
            var explicitTarget = MemberResolver.ResolveMethod(s[7..], context, wantConstructor: false);
            return explicitTarget.Method is null
                ? throw new ReplException("an .override target must be a method of a base type or an interface, not a session method")
                : explicitTarget;
        }

        if (!s.Contains("::", StringComparison.Ordinal))
        {
            throw new ReplException("usage: .override T::M  (or .override method instance RetType T::M(params))");
        }

        // The short form names the target; the implementing method's own signature picks the overload.
        var parameters = string.Join(", ", implementing.ParameterTypes.Select(TypeNameFormatter.Pretty));
        var instance = implementing.IsStatic ? "" : "instance ";
        var spelled = $"{instance}{TypeNameFormatter.Pretty(implementing.ReturnType)} {s}({parameters})";
        var target = MemberResolver.ResolveMethod(spelled, context, wantConstructor: false);
        return target.Method is null
            ? throw new ReplException("an .override target must be a method of a base type or an interface, not a session method")
            : target;
    }

    /// <summary>
    /// Describes a resolved override target without asking a builder for its parameters.
    /// </summary>
    /// <param name="target">The target.</param>
    /// <returns>The description.</returns>
    public static string Describe(ResolvedMethod target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return target.Declared is { } declared ? $"{declared.DescribeMember()} on {TypeNameFormatter.Pretty(target.DeclaringType)}" : MemberResolver.Describe(target.Method!);
    }

    private static void Check(ResolvedMethod target, MethodSignature method)
    {
        var targetIsVirtual = target.Declared is { } declared ? declared.Attributes.HasFlag(MethodAttributes.Virtual) : target.Method!.IsVirtual;
        if (!targetIsVirtual)
        {
            throw new ReplException($"{Describe(target)} is not virtual, so nothing can override it");
        }

        if (!method.Attributes.HasFlag(MethodAttributes.Virtual))
        {
            throw new ReplException($"{method.Name} must be virtual to .override {Describe(target)}");
        }

        if (target.IsStatic != method.IsStatic)
        {
            throw new ReplException($"{method.Name} is {(method.IsStatic ? "static" : "an instance method")} but {Describe(target)} is not");
        }

        var expected = target.ParameterTypes;
        if (expected.Count != method.Parameters.Count || !expected.Zip(method.ParameterTypes).All(p => TypeIdentity.Equal(p.First, p.Second)) || !TypeIdentity.Equal(target.ReturnType, method.ReturnType))
        {
            throw new ReplException($".override target {Describe(target)} does not match {method.DescribeMember()}");
        }
    }
}
