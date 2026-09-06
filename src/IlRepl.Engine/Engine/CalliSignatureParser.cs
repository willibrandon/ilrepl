using System.Reflection;
using System.Runtime.InteropServices;

namespace IlRepl.Engine;

/// <summary>
/// Parses the operand of <c>calli</c>: <c>[instance] [vararg] RetType(Params)</c> for managed
/// pointers and <c>unmanaged [cdecl|stdcall|thiscall|fastcall] RetType(Params)</c> for native ones.
/// </summary>
public static class CalliSignatureParser
{
    /// <summary>
    /// Parses a calli signature.
    /// </summary>
    /// <param name="spec">The signature text.</param>
    /// <param name="context">The parse context.</param>
    /// <returns>The parsed signature.</returns>
    /// <exception cref="ReplException">The signature is malformed.</exception>
    public static CalliSignature Parse(string spec, ParseContext context)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(context);
        var s = TypeParser.Normalize(spec).Trim();
        if (s.Length == 0)
        {
            throw new ReplException("calli needs a signature, e.g. calli int32(int32, int32)");
        }

        var pos = 0;
        var isUnmanaged = false;
        var unmanagedConvention = CallingConvention.Winapi;
        var managed = CallingConventions.Standard;

        while (true)
        {
            TypeParser.SkipWhitespace(s, ref pos);
            if (TypeParser.TryKeyword(s, ref pos, "instance"))
            {
                managed |= CallingConventions.HasThis;
            }
            else if (TypeParser.TryKeyword(s, ref pos, "explicit"))
            {
                managed |= CallingConventions.ExplicitThis;
            }
            else if (TypeParser.TryKeyword(s, ref pos, "vararg"))
            {
                managed = (managed & ~CallingConventions.Standard) | CallingConventions.VarArgs;
            }
            else if (TypeParser.TryKeyword(s, ref pos, "unmanaged"))
            {
                isUnmanaged = true;
            }
            else if (TypeParser.TryKeyword(s, ref pos, "cdecl"))
            {
                isUnmanaged = true;
                unmanagedConvention = CallingConvention.Cdecl;
            }
            else if (TypeParser.TryKeyword(s, ref pos, "stdcall"))
            {
                isUnmanaged = true;
                unmanagedConvention = CallingConvention.StdCall;
            }
            else if (TypeParser.TryKeyword(s, ref pos, "thiscall"))
            {
                isUnmanaged = true;
                unmanagedConvention = CallingConvention.ThisCall;
            }
            else if (TypeParser.TryKeyword(s, ref pos, "fastcall"))
            {
                isUnmanaged = true;
                unmanagedConvention = CallingConvention.FastCall;
            }
            else if (TypeParser.TryKeyword(s, ref pos, "default"))
            {
                // The default managed convention; nothing to change.
            }
            else
            {
                break;
            }
        }

        var open = s.IndexOf('(', pos);
        if (open < 0)
        {
            throw new ReplException("calli needs a parameter list in parentheses");
        }

        var close = TypeParser.FindMatchingParen(s, open);
        var returnType = TypeParser.Parse(s[pos..open], context);
        var parameters = new List<Type>();
        List<Type>? optional = null;
        foreach (var part in TypeParser.SplitTopLevel(s.Substring(open + 1, close - open - 1)))
        {
            if (part == "...")
            {
                if (optional is not null)
                {
                    throw new ReplException("only one '...' is allowed in a signature");
                }

                optional = [];
                managed = (managed & ~CallingConventions.Standard) | CallingConventions.VarArgs;
                continue;
            }

            var t = TypeParser.Parse(part, context);
            if (optional is null)
            {
                parameters.Add(t);
            }
            else
            {
                optional.Add(t);
            }
        }

        if (close + 1 < s.Length && s[(close + 1)..].Trim().Length > 0)
        {
            throw new ReplException($"unexpected '{s[(close + 1)..].Trim()}' after calli signature");
        }

        return new CalliSignature(isUnmanaged, unmanagedConvention, managed, returnType, [.. parameters], optional?.ToArray());
    }
}
