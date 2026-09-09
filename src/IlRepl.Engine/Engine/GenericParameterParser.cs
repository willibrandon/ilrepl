using System.Reflection;

namespace IlRepl.Engine;

/// <summary>
/// Parses variance, special constraints and type constraints in class and method generic parameter declarations.
/// </summary>
public static class GenericParameterParser
{
    /// <summary>
    /// Parses the text between the angle brackets.
    /// </summary>
    /// <param name="inner">The list without its brackets.</param>
    /// <returns>The parameters in order.</returns>
    /// <exception cref="ReplException">The list is malformed.</exception>
    public static IReadOnlyList<GenericParameterSpec> Parse(string inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        var result = new List<GenericParameterSpec>();
        foreach (var raw in TypeParser.SplitTopLevel(inner))
        {
            var part = raw.Trim();
            var attributes = GenericParameterAttributes.None;
            if (part.StartsWith('+'))
            {
                attributes |= GenericParameterAttributes.Covariant;
                part = part[1..].TrimStart();
            }
            else if (part.StartsWith('-'))
            {
                attributes |= GenericParameterAttributes.Contravariant;
                part = part[1..].TrimStart();
            }

            var constraints = new List<string>();
            while (true)
            {
                var pos = 0;
                if (TypeParser.TryKeyword(part, ref pos, "class"))
                {
                    attributes |= GenericParameterAttributes.ReferenceTypeConstraint;
                }
                else if (TypeParser.TryKeyword(part, ref pos, "valuetype"))
                {
                    attributes |= GenericParameterAttributes.NotNullableValueTypeConstraint;
                }
                else if (TypeParser.TryKeyword(part, ref pos, ".ctor"))
                {
                    attributes |= GenericParameterAttributes.DefaultConstructorConstraint;
                }
                else if (TypeParser.TryKeyword(part, ref pos, "byreflike"))
                {
                    throw new ReplException("byreflike constraints are not supported on session types");
                }
                else
                {
                    break;
                }

                part = part[pos..].TrimStart();
            }

            if (part.StartsWith('('))
            {
                var close = TypeParser.FindMatchingParen(part, 0);
                constraints.AddRange(TypeParser.SplitTopLevel(part[1..close]));
                part = part[(close + 1)..].Trim();
            }

            var name = InstructionParser.Unquote(part);
            if (!InstructionParser.IsIdentifier(name))
            {
                throw new ReplException($"bad generic parameter name '{part}'");
            }

            if (result.Any(r => r.Name == name))
            {
                throw new ReplException($"generic parameter '{name}' is declared twice");
            }

            if (attributes.HasFlag(GenericParameterAttributes.ReferenceTypeConstraint) && attributes
                .HasFlag(GenericParameterAttributes.NotNullableValueTypeConstraint))
            {
                throw new ReplException($"generic parameter '{name}' cannot be both class and valuetype");
            }

            result.Add(new GenericParameterSpec(name, attributes, constraints));
        }

        return result;
    }
}
