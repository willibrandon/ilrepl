using System.Reflection;
using IlRepl.Engine.Binding;

namespace IlRepl.Engine;

/// <summary>
/// Resolves ILAsm member references through shared syntax and binding rules to exact runtime members.
/// </summary>
/// <remarks>
/// Resolves ILAsm method and field references against loaded types. The return type and the
/// <c>[assembly]</c> prefix are optional, short type names resolve through the common
/// <c>System.*</c> namespaces, and <c>!N</c>/<c>!!N</c> inside the reference follow ILAsm rules.
/// The grammar is <see cref="CilSyntaxParser"/>'s and the overload decisions are
/// <see cref="SymbolBinder"/>'s; this entry point binds in the runtime scope and hands back the
/// reflection objects the emitter takes.
/// </remarks>
public static class MemberResolver
{
    /// <summary>
    /// Resolves a method reference such as <c>void [System.Console]System.Console::WriteLine(string)</c>,
    /// <c>instance string Object::ToString()</c>, <c>Console::WriteLine(string)</c>, <c>instance void StringBuilder::.ctor()</c>,
    /// <c>!!0 Enumerable::First&lt;int32&gt;(class IEnumerable`1&lt;!!0&gt;)</c>, or <c>vararg int32 Hello::CountArgs(..., int32, int32)</c>.
    /// </summary>
    /// <param name="spec">The reference text.</param>
    /// <param name="context">The parse context.</param>
    /// <param name="wantConstructor">True when the call site is <c>newobj</c> and a constructor is required.</param>
    /// <returns>The resolved method.</returns>
    /// <exception cref="ReplException">The reference is malformed, or no unique member matches.</exception>
    public static ResolvedMethod ResolveMethod(string spec, ParseContext context, bool wantConstructor)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(context);
        var syntax = CilSyntaxParser.ParseMethodReference(TypeParser.Normalize(spec).Trim());
        var scope = new RuntimeBindingScope(context);
        var bound = SymbolBinder.BindMethodReference(syntax, scope, wantConstructor);
        return new RuntimeBindingAdapter(scope).ToResolvedMethod(bound);
    }

    /// <summary>
    /// Resolves a field reference such as <c>string [System.Runtime]System.String::Empty</c> or <c>int32 Counter::Count</c>.
    /// </summary>
    /// <param name="spec">The reference text.</param>
    /// <param name="context">The parse context.</param>
    /// <returns>The field.</returns>
    /// <exception cref="ReplException">The reference is malformed or the field does not exist.</exception>
    public static FieldInfo ResolveField(string spec, ParseContext context)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(context);
        var syntax = CilSyntaxParser.ParseFieldReference(TypeParser.Normalize(spec).Trim());
        var scope = new RuntimeBindingScope(context);
        var field = SymbolBinder.BindFieldReference(syntax, scope);
        return new RuntimeBindingAdapter(scope).ToField(field);
    }

    /// <summary>
    /// Renders a method in the same shape the resolver accepts, for candidate lists and
    /// diagnostics.
    /// </summary>
    /// <param name="method">The method or constructor.</param>
    /// <returns>The IL-style signature.</returns>
    public static string Describe(MethodBase method)
    {
        ArgumentNullException.ThrowIfNull(method);
        string parameters;
        try
        {
            parameters = string.Join(", ", method.GetParameters().Select(p => TypeNameFormatter.Pretty(p.ParameterType)));
        }
        catch (NotSupportedException)
        {
            // A builder cannot list its parameters before its type is created.
            return $"{TypeNameFormatter.Pretty(method.DeclaringType)}::{method.Name}";
        }

        if (method.CallingConvention.HasFlag(CallingConventions.VarArgs))
        {
            parameters = parameters.Length == 0 ? "..." : parameters + ", ...";
        }

        var returnType = method is MethodInfo mi ? TypeNameFormatter.Pretty(mi.ReturnType) : "void";
        var instance = method.IsStatic ? "" : "instance ";
        var name = method is ConstructorInfo ? ".ctor" : method.Name;
        if (method is MethodInfo g && g.IsGenericMethod)
        {
            name += "<" + string.Join(", ", g.GetGenericArguments().Select(TypeNameFormatter.Pretty)) + ">";
        }

        return $"{instance}{returnType} {TypeNameFormatter.Pretty(method.DeclaringType)}::{name}({parameters})";
    }

    internal static bool TypesEqual(Type a, Type b) => TypeIdentity.Equal(a, b);
}
