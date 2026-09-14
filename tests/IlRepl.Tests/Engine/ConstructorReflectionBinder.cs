using System.Globalization;
using System.Reflection;

namespace IlRepl.Tests.Engine;

/// <summary>
/// A real custom constructor binder records selection and delegates valid overload binding to the framework.
/// </summary>
internal sealed class ConstructorReflectionBinder : Binder
{
    /// <summary>
    /// Counts actual custom method-selection callbacks.
    /// </summary>
    public int Calls;

    /// <summary>
    /// Selects the actual constructor while recording user binder execution.
    /// </summary>
    /// <param name="bindingAttr">The binding flags.</param>
    /// <param name="match">The candidate methods.</param>
    /// <param name="types">The required parameter types.</param>
    /// <param name="modifiers">The parameter modifiers.</param>
    /// <returns>The framework-selected method.</returns>
    public override MethodBase? SelectMethod(BindingFlags bindingAttr, MethodBase[] match, Type[] types, ParameterModifier[]? modifiers)
    {
        Calls++;
        return Type.DefaultBinder.SelectMethod(bindingAttr, match, types, modifiers);
    }

    /// <summary>
    /// Rejects field binding because the fixture invokes only constructor selection.
    /// </summary>
    /// <param name="bindingAttr">The binding flags.</param>
    /// <param name="match">The candidate fields.</param>
    /// <param name="value">The proposed value.</param>
    /// <param name="culture">The binding culture.</param>
    /// <returns>No value; this operation is unsupported.</returns>
    public override FieldInfo BindToField(BindingFlags bindingAttr, FieldInfo[] match, object value, CultureInfo? culture)
        => throw new NotSupportedException("unexpected field binding");

    /// <summary>
    /// Rejects invocation binding because constructor lookup uses SelectMethod.
    /// </summary>
    /// <param name="bindingAttr">The binding flags.</param>
    /// <param name="match">The candidate methods.</param>
    /// <param name="args">The argument values.</param>
    /// <param name="modifiers">The parameter modifiers.</param>
    /// <param name="culture">The binding culture.</param>
    /// <param name="names">The argument names.</param>
    /// <param name="state">The binding state.</param>
    /// <returns>No value; this operation is unsupported.</returns>
    public override MethodBase BindToMethod(BindingFlags bindingAttr, MethodBase[] match, ref object?[] args,
        ParameterModifier[]? modifiers, CultureInfo? culture, string[]? names, out object? state)
        => throw new NotSupportedException("unexpected invocation binding");

    /// <summary>
    /// Rejects argument coercion because the fixture supplies the exact integer type.
    /// </summary>
    /// <param name="value">The supplied value.</param>
    /// <param name="type">The requested type.</param>
    /// <param name="culture">The binding culture.</param>
    /// <returns>No value; this operation is unsupported.</returns>
    public override object ChangeType(object value, Type type, CultureInfo? culture)
        => throw new NotSupportedException("unexpected argument conversion");

    /// <summary>
    /// Rejects argument reordering because the fixture has one positional argument.
    /// </summary>
    /// <param name="args">The supplied arguments.</param>
    /// <param name="state">The binding state.</param>
    public override void ReorderArgumentArray(ref object?[] args, object state)
        => throw new NotSupportedException("unexpected argument reordering");

    /// <summary>
    /// Rejects property binding because the fixture selects constructors.
    /// </summary>
    /// <param name="bindingAttr">The binding flags.</param>
    /// <param name="match">The candidate properties.</param>
    /// <param name="returnType">The requested return type.</param>
    /// <param name="indexes">The index argument types.</param>
    /// <param name="modifiers">The parameter modifiers.</param>
    /// <returns>No value; this operation is unsupported.</returns>
    public override PropertyInfo? SelectProperty(BindingFlags bindingAttr, PropertyInfo[] match, Type? returnType,
        Type[]? indexes, ParameterModifier[]? modifiers) => throw new NotSupportedException("unexpected property binding");
}
