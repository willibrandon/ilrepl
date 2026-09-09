using System.Globalization;
using System.Reflection;
using System.Text;

namespace IlRepl.Engine.Binding;

/// <summary>
/// Renders symbolic types and members using the REPL's runtime-compatible spellings.
/// </summary>
/// <remarks>
/// Spells symbols the way the REPL spells runtime types: the short, IL-flavored form the stack
/// column and messages use, and the shapes ildasm prints for the forms reflection cannot carry.
/// A symbol imported from a runtime type renders as <see cref="TypeNameFormatter.Pretty(Type)"/>
/// renders the type.
/// </remarks>
public static class SymbolRenderer
{
    /// <summary>
    /// A short, IL-flavored name: <c>int32</c>, <c>string</c>, <c>List&lt;int32&gt;</c>, <c>int32[]</c>, <c>int32&amp;</c>.
    /// </summary>
    /// <param name="type">The type, or null for an unknown stack entry.</param>
    /// <returns>The display name.</returns>
    public static string Pretty(TypeSymbol? type)
    {
        if (type is null)
        {
            return "?";
        }

        switch (type.Kind)
        {
            case TypeSymbolKind.Primitive:
                return type.Keyword!;
            case TypeSymbolKind.TypeParameter:
                return "!" + type.Name;
            case TypeSymbolKind.MethodParameter:
                return "!!" + type.Name;
            case TypeSymbolKind.ByRef:
                return Pretty(type.Element) + "&";
            case TypeSymbolKind.Pointer:
                return Pretty(type.Element) + "*";
            case TypeSymbolKind.FunctionPointer:
                return FunctionPointer(type.Signature!, Pretty);
            case TypeSymbolKind.SzArray:
                return Pretty(type.Element) + "[]";
            case TypeSymbolKind.Array:
                return Pretty(type.Element) + ArraySignatureShape.Render(type.Rank, type.Sizes, type.LowerBounds);
            case TypeSymbolKind.Modified:
            case TypeSymbolKind.Pinned:
                return Pretty(type.Element);
            case TypeSymbolKind.Constructed:
                return StripArity(type.Element!.Name) + "<" + string.Join(", ", type.Arguments.Select(Pretty)) + ">";
            case TypeSymbolKind.Named:
                if (type.GenericParameterNames.Count > 0)
                {
                    return StripArity(type.Name) + "<" + string.Join(", ", type.GenericParameterNames.Select(n => "!" + n)) + ">";
                }

                return type.Declaring is not null ? Pretty(type.Declaring) + "/" + type.Name : type.Name;
            case TypeSymbolKind.Unresolved:
                return type.Name;
            default:
                return "?";
        }
    }

    /// <summary>
    /// The name reflection reports for a definition: <c>Namespace.Outer+Inner</c>, arity suffix included.
    /// </summary>
    /// <param name="type">A named definition or a construction.</param>
    /// <returns>The name.</returns>
    public static string ReflectionFullName(TypeSymbol type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var definition = type.DefinitionOrSelf;
        if (definition.Kind == TypeSymbolKind.Primitive)
        {
            return CilPrimitives.CoreLibNameOf(definition.Keyword!);
        }

        if (definition.Declaring is not null)
        {
            return ReflectionFullName(definition.Declaring) + "+" + definition.Name;
        }

        return definition.Namespace.Length == 0 ? definition.Name : definition.Namespace + "." + definition.Name;
    }

    /// <summary>
    /// The ILAsm path of a definition: <c>Namespace.Outer/Inner</c>.
    /// </summary>
    /// <param name="type">A named definition or a construction.</param>
    /// <returns>The path.</returns>
    public static string IlPath(TypeSymbol type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var definition = type.DefinitionOrSelf;
        if (definition.Kind == TypeSymbolKind.Primitive)
        {
            return CilPrimitives.CoreLibNameOf(definition.Keyword!);
        }

        if (definition.Declaring is not null)
        {
            return IlPath(definition.Declaring) + "/" + definition.Name;
        }

        return definition.Namespace.Length == 0 ? definition.Name : definition.Namespace + "." + definition.Name;
    }

    /// <summary>
    /// Renders a member reference in the spelling accepted by the resolver.
    /// </summary>
    /// <remarks>
    /// Renders a member in the shape the resolver accepts, for candidate lists and diagnostics:
    /// <c>instance string Object::ToString()</c>.
    /// </remarks>
    /// <param name="method">The member.</param>
    /// <returns>The IL-style signature.</returns>
    public static string Describe(MethodSymbol method) => Describe(method, Pretty);

    /// <summary>
    /// Renders a member the way <see cref="Describe(MethodSymbol)"/> does, spelling types through the given function.
    /// </summary>
    /// <param name="method">The member.</param>
    /// <param name="pretty">Spells a type.</param>
    /// <returns>The IL-style signature.</returns>
    public static string Describe(MethodSymbol method, Func<TypeSymbol?, string> pretty)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(pretty);
        var parameters = string.Join(", ", method.Parameters.Select(p => pretty(p.Type)));
        if (method.IsVarArg)
        {
            parameters = parameters.Length == 0 ? "..." : parameters + ", ...";
        }

        var returnType = method.IsConstructor ? "void" : pretty(method.ReturnType);
        var instance = method.IsStatic ? "" : "instance ";
        var name = method.Name == ".cctor" ? ".ctor" : method.Name;
        if (method.GenericArguments.Count > 0)
        {
            name += "<" + string.Join(", ", method.GenericArguments.Select(pretty)) + ">";
        }
        else if (method.GenericParameters.Count > 0)
        {
            name += "<" + string.Join(", ", method.GenericParameters.Select(p => "!!" + p.Name)) + ">";
        }

        return $"{instance}{returnType} {pretty(method.DeclaringType)}::{name}({parameters})";
    }

    /// <summary>
    /// Renders a declared signature the way a call names it: <c>int32 Fib(int32)</c>, <c>!!T Make&lt;T&gt;()</c>.
    /// </summary>
    /// <param name="method">The member.</param>
    /// <param name="pretty">Spells a type.</param>
    /// <returns>The call form.</returns>
    public static string DescribeSignature(MethodSymbol method, Func<TypeSymbol?, string> pretty)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(pretty);
        var generic = method.GenericParameters.Count == 0 ? "" : "<" + string.Join(", ", method.GenericParameters.Select(p => p.Name))
            + ">";
        return $"{pretty(method.ReturnType)} {method.Name}{generic}({string.Join(", ", method.Parameters.Select(p => pretty(p.Type)))})";
    }

    /// <summary>
    /// Renders a declared member the way a listing shows it: <c>instance int32 Sum()</c>, <c>static int32 Make(int32)</c>.
    /// </summary>
    /// <param name="method">The member.</param>
    /// <param name="pretty">Spells a type.</param>
    /// <returns>The member form.</returns>
    public static string DescribeMember(MethodSymbol method, Func<TypeSymbol?, string> pretty)
    {
        ArgumentNullException.ThrowIfNull(method);
        return (method.IsStatic ? "static " : "instance ") + DescribeSignature(method, pretty);
    }

    /// <summary>
    /// A standalone signature as <c>calli</c> or a function pointer spells it, without the <c>method</c> word.
    /// </summary>
    /// <param name="signature">The signature.</param>
    /// <param name="pretty">Spells a type.</param>
    /// <returns>The text.</returns>
    public static string Signature(MethodSignatureSymbol signature, Func<TypeSymbol?, string> pretty)
    {
        ArgumentNullException.ThrowIfNull(signature);
        ArgumentNullException.ThrowIfNull(pretty);
        var sb = new StringBuilder();
        if (signature.HasThis)
        {
            sb.Append("instance ");
        }

        if (signature.ExplicitThis)
        {
            sb.Append("explicit ");
        }

        if (signature.IsVarArg)
        {
            sb.Append("vararg ");
        }

        if (signature.IsUnmanaged)
        {
            sb.Append("unmanaged ");
            var word = signature.UnmanagedConvention switch
            {
                System.Runtime.InteropServices.CallingConvention.Cdecl => "cdecl ",
                System.Runtime.InteropServices.CallingConvention.StdCall => "stdcall ",
                System.Runtime.InteropServices.CallingConvention.ThisCall => "thiscall ",
                System.Runtime.InteropServices.CallingConvention.FastCall => "fastcall ",
                _ => "",
            };
            sb.Append(word);
        }

        sb.Append(pretty(signature.ReturnType));
        sb.Append('(');
        var parts = new List<string>();
        for (var i = 0; i < signature.Parameters.Count; i++)
        {
            if (signature.SentinelIndex == i)
            {
                parts.Add("...");
            }

            parts.Add(pretty(signature.Parameters[i]));
        }

        if (signature.SentinelIndex == signature.Parameters.Count)
        {
            parts.Add("...");
        }

        sb.Append(string.Join(", ", parts));
        sb.Append(')');
        return sb.ToString();
    }

    private static string FunctionPointer(MethodSignatureSymbol signature, Func<TypeSymbol?, string> pretty)
    {
        var text = Signature(signature, pretty);
        var paren = text.IndexOf('(', StringComparison.Ordinal);
        return "method " + text[..paren] + " *" + text[paren..];
    }

    private static string StripArity(string name)
    {
        var tick = name.IndexOf('`', StringComparison.Ordinal);
        return tick > 0 ? name[..tick] : name;
    }

    /// <summary>
    /// The ILAsm access word for method attributes.
    /// </summary>
    /// <param name="attributes">The attributes.</param>
    /// <returns>The word.</returns>
    public static string AccessWord(MethodAttributes attributes) => MemberAccess.AccessWord(attributes);

    /// <summary>
    /// An integer as ILAsm writes it.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <returns>The text.</returns>
    public static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
