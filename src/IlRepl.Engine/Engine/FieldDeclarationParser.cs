using System.Reflection;
using IlRepl.Engine.Binding;

namespace IlRepl.Engine;

/// <summary>
/// Parses field declarations and materializes their types and constants for the runtime.
/// </summary>
public static class FieldDeclarationParser
{
    private const string Usage = "usage: .field [public] [static] [initonly|literal] T name [= int32(5)]";

    /// <summary>
    /// Parses the text after <c>.field</c>.
    /// </summary>
    /// <param name="spec">The declaration text.</param>
    /// <param name="context">The parse context of the open type, with its generic parameters in scope.</param>
    /// <param name="source">The line as typed.</param>
    /// <returns>The field, without the checks that need the whole type.</returns>
    /// <exception cref="ReplException">The line is malformed or uses a feature session fields do not have.</exception>
    public static FieldDeclaration Parse(string spec, ParseContext context, string source)
    {
        var scope = new Binding.RuntimeBindingScope(context);
        var adapter = new Binding.RuntimeBindingAdapter(scope);
        var declaration = Binding.FieldDeclarationBinding.Parse(spec, scope, source);
        var type = adapter.ToType(declaration.Type.Type);
        var name = declaration.Name;
        var attributes = declaration.Attributes;
        var offset = declaration.Offset;
        var constantText = declaration.ConstantText;
        var required = adapter.ToTypes(declaration.Type.RequiredModifiers);
        var optional = adapter.ToTypes(declaration.Type.OptionalModifiers);
        object? constant = null;
        var hasDefault = false;
        if (constantText is not null)
        {
            if (constantText.Length == 0)
            {
                throw new ReplException($"field {name} needs a value after '='");
            }

            var constantTarget = Nullable.GetUnderlyingType(type) ?? type;
            if (!(constantTarget.IsPrimitive || constantTarget.IsEnum || constantTarget == typeof(string) || constantTarget == typeof(object) || constantTarget == typeof(decimal) || !constantTarget.IsValueType))
            {
                throw new ReplException($"a constant must be a primitive, string, or enum; {TypeNameFormatter.Pretty(type)} is neither");
            }

            constant = ConstantParser.Parse(constantText, type, "field " + name);
            hasDefault = true;
            attributes |= FieldAttributes.HasDefault;
        }

        return new FieldDeclaration(name, type, attributes, offset, constant, hasDefault, required, optional, [], source)
        {
            ExactType = RuntimeSymbolTypes.RequiresExact(declaration.Type.ExactType) ? declaration.Type.ExactType : null,
        };
    }

}
