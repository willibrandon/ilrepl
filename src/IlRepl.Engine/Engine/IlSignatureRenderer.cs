using System.Globalization;
using System.Reflection.Metadata;
using System.Text;

namespace IlRepl.Engine;

/// <summary>
/// Spells <see cref="IlSignature"/> trees the way the REPL spells types: leaves through
/// <see cref="TypeNameFormatter"/>, so a resolved type reads exactly as <c>.il</c> prints it,
/// and the composite forms reflection cannot carry (modifiers, array shapes, function pointers,
/// pinning) the way ildasm prints them.
/// </summary>
public static class IlSignatureRenderer
{
    /// <summary>
    /// The fully qualified ILAsm spelling.
    /// </summary>
    /// <param name="signature">The signature.</param>
    /// <returns>The text.</returns>
    public static string IlAsm(IlSignature signature) => Render(signature, pretty: false, namedParameters: false);

    /// <summary>
    /// The fully qualified spelling with generic parameters by name where the context knew them,
    /// for method headers and locals, where ildasm writes <c>!T</c> rather than <c>!0</c>.
    /// </summary>
    /// <param name="signature">The signature.</param>
    /// <returns>The text.</returns>
    public static string IlAsmNamed(IlSignature signature) => Render(signature, pretty: false, namedParameters: true);

    /// <summary>
    /// The short spelling the stack column and <c>.locals</c> listing use.
    /// </summary>
    /// <param name="signature">The signature.</param>
    /// <returns>The text.</returns>
    public static string Pretty(IlSignature signature) => Render(signature, pretty: true, namedParameters: true);

    /// <summary>
    /// A method signature as <c>calli</c> takes it: <c>[instance] [explicit] [vararg] ret(params)</c> or
    /// <c>unmanaged cdecl ret(params)</c>.
    /// </summary>
    /// <param name="signature">The signature.</param>
    /// <returns>The text.</returns>
    public static string IlAsm(IlMethodSignature signature)
    {
        ArgumentNullException.ThrowIfNull(signature);
        return Convention(signature) + Render(signature.ReturnType, false, false) + "(" + Parameters(signature, false, false) + ")";
    }

    private static string Convention(IlMethodSignature signature)
    {
        var sb = new StringBuilder();
        if (signature.HasThis)
        {
            sb.Append("instance ");
        }

        if (signature.ExplicitThis)
        {
            sb.Append("explicit ");
        }

        switch (signature.Convention)
        {
            case SignatureCallingConvention.VarArgs:
                sb.Append("vararg ");
                break;
            case SignatureCallingConvention.CDecl:
                sb.Append("unmanaged cdecl ");
                break;
            case SignatureCallingConvention.StdCall:
                sb.Append("unmanaged stdcall ");
                break;
            case SignatureCallingConvention.ThisCall:
                sb.Append("unmanaged thiscall ");
                break;
            case SignatureCallingConvention.FastCall:
                sb.Append("unmanaged fastcall ");
                break;
            case SignatureCallingConvention.Unmanaged:
                sb.Append("unmanaged ");
                break;
            default:
                break;
        }

        return sb.ToString();
    }

    private static string Parameters(IlMethodSignature signature, bool pretty, bool named)
    {
        var parts = new List<string>();
        for (var i = 0; i < signature.Parameters.Count; i++)
        {
            if (i == signature.RequiredParameterCount && signature.IsVarArg)
            {
                parts.Add("...");
            }

            parts.Add(Render(signature.Parameters[i], pretty, named));
        }

        if (signature.IsVarArg && signature.RequiredParameterCount == signature.Parameters.Count)
        {
            parts.Add("...");
        }

        return string.Join(", ", parts);
    }

    private static string Render(IlSignature signature, bool pretty, bool namedParameters)
    {
        ArgumentNullException.ThrowIfNull(signature);
        switch (signature.Kind)
        {
            case IlSignatureKind.Primitive:
                return signature.Keyword!;
            case IlSignatureKind.Named:
                if (signature.Resolved is { } type)
                {
                    return pretty ? TypeNameFormatter.Pretty(type) : TypeNameFormatter.IlAsm(type);
                }

                return pretty ? Unqualified(signature.UnresolvedName!) : (signature.IsValueType ? "valuetype " : "class ") + signature.UnresolvedName;
            case IlSignatureKind.GenericInstance:
            {
                var definition = signature.Element!;
                var arguments = string.Join(", ", signature.Arguments.Select(a => Render(a, pretty, namedParameters)));
                if (pretty)
                {
                    // The definition's own name without its parameters, nested under its declaring type as Pretty does.
                    var name = definition.Resolved is { } d
                        ? (d.IsNested && d.DeclaringType is not null ? TypeNameFormatter.Pretty(d.DeclaringType) + "/" : "") + d.Name
                        : Unqualified(definition.UnresolvedName!);
                    var tick = name.LastIndexOf('`');
                    return (tick > 0 ? name[..tick] : name) + "<" + arguments + ">";
                }

                var head = definition.Resolved is { } dt ? TypeNameFormatter.IlAsmDefinition(dt) : (definition.IsValueType ? "valuetype " : "class ") + definition.UnresolvedName;
                return head + "<" + arguments + ">";
            }

            case IlSignatureKind.SzArray:
                return Render(signature.Element!, pretty, namedParameters) + "[]";
            case IlSignatureKind.Array:
                return Render(signature.Element!, pretty, namedParameters) + ArrayShape(signature);
            case IlSignatureKind.ByRef:
                return Render(signature.Element!, pretty, namedParameters) + "&";
            case IlSignatureKind.Pointer:
                return Render(signature.Element!, pretty, namedParameters) + "*";
            case IlSignatureKind.FunctionPointer:
            {
                var method = signature.Method!;
                return "method " + Convention(method) + Render(method.ReturnType, pretty, namedParameters) + " *(" + Parameters(method, pretty, namedParameters) + ")";
            }

            case IlSignatureKind.Modified:
                return Render(signature.Element!, pretty, namedParameters) + (signature.IsRequired ? " modreq(" : " modopt(") + Modifier(signature.Modifier!, pretty) + ")";
            case IlSignatureKind.Pinned:
                return Render(signature.Element!, pretty, namedParameters) + " pinned";
            case IlSignatureKind.TypeParameter:
                return "!" + ParameterName(signature, namedParameters);
            case IlSignatureKind.MethodParameter:
                return "!!" + ParameterName(signature, namedParameters);
            case IlSignatureKind.Sentinel:
                return "...";
            default:
                return "?";
        }
    }

    private static string ParameterName(IlSignature signature, bool named) =>
        named && signature.Resolved is { IsGenericParameter: true } p ? p.Name : signature.Index.ToString(CultureInfo.InvariantCulture);

    private static string Modifier(IlSignature modifier, bool pretty)
    {
        // A modifier is a type reference without its class/valuetype word.
        var text = Render(modifier, pretty, false);
        if (text.StartsWith("class ", StringComparison.Ordinal))
        {
            return text[6..];
        }

        return text.StartsWith("valuetype ", StringComparison.Ordinal) ? text[10..] : text;
    }

    private static string ArrayShape(IlSignature array)
    {
        var dimensions = new List<string>();
        for (var i = 0; i < array.Rank; i++)
        {
            int? lower = i < array.LowerBounds.Count ? array.LowerBounds[i] : null;
            int? size = i < array.Sizes.Count ? array.Sizes[i] : null;
            var text = (lower, size) switch
            {
                (int l, int s) => l.ToString(CultureInfo.InvariantCulture) + "..." + (l + s - 1).ToString(CultureInfo.InvariantCulture),
                (int l, null) => l.ToString(CultureInfo.InvariantCulture) + "...",
                (null, int s) => s.ToString(CultureInfo.InvariantCulture),
                _ => "...",
            };
            dimensions.Add(text);
        }

        return "[" + string.Join(",", dimensions) + "]";
    }

    private static string Unqualified(string name)
    {
        var close = name.IndexOf(']', StringComparison.Ordinal);
        var bare = name.StartsWith('[') && close > 0 ? name[(close + 1)..] : name;
        var dot = bare.LastIndexOf('.');
        return dot < 0 ? bare : bare[(dot + 1)..];
    }
}
