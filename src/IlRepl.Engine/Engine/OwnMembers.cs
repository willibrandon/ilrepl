using System.Reflection;

namespace IlRepl.Engine;

/// <summary>
/// The members a type being written has declared so far, keyed by the prototype builders the
/// stack model sees. A builder cannot describe itself before its type is created, so every
/// lookup goes through the declarations kept here.
/// </summary>
public sealed class OwnMembers
{
    private readonly List<(FieldDeclaration Declaration, FieldInfo Builder)> _fields = [];
    private readonly List<(MethodSignature Signature, MethodBase Builder, bool Declared)> _methods = [];

    /// <summary>
    /// Defines a builder for a method referenced before its declaration, or null when the type
    /// cannot take forward references.
    /// </summary>
    public Func<MethodSignature, MethodBase>? DefineForward { get; set; }

    /// <summary>
    /// The fields declared so far.
    /// </summary>
    public IReadOnlyList<(FieldDeclaration Declaration, FieldInfo Builder)> Fields => _fields;

    /// <summary>
    /// The methods declared so far, and the ones referenced before their declaration.
    /// </summary>
    public IReadOnlyList<(MethodSignature Signature, MethodBase Builder, bool Declared)> Methods => _methods;

    /// <summary>
    /// Adds a field.
    /// </summary>
    /// <param name="declaration">The declaration.</param>
    /// <param name="builder">The prototype builder.</param>
    public void Add(FieldDeclaration declaration, FieldInfo builder)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        ArgumentNullException.ThrowIfNull(builder);
        _fields.Add((declaration, builder));
    }

    /// <summary>
    /// Adds a method, or marks a forward reference as declared.
    /// </summary>
    /// <param name="signature">The signature.</param>
    /// <param name="builder">The prototype builder.</param>
    /// <param name="declared">True when the method's header was seen; false for a forward reference.</param>
    public void Add(MethodSignature signature, MethodBase builder, bool declared)
    {
        ArgumentNullException.ThrowIfNull(signature);
        ArgumentNullException.ThrowIfNull(builder);
        var index = _methods.FindIndex(m => ReferenceEquals(m.Builder, builder));
        if (index >= 0)
        {
            _methods[index] = (signature, builder, declared || _methods[index].Declared);
        }
        else
        {
            _methods.Add((signature, builder, declared));
        }
    }

    /// <summary>
    /// Finds a field by name.
    /// </summary>
    /// <param name="name">The field name.</param>
    /// <returns>The field, or null.</returns>
    public (FieldDeclaration Declaration, FieldInfo Builder)? FindField(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        foreach (var field in _fields)
        {
            if (field.Declaration.Name == name)
            {
                return field;
            }
        }

        return null;
    }

    /// <summary>
    /// Finds the methods with a name.
    /// </summary>
    /// <param name="name">The method name.</param>
    /// <returns>The candidates.</returns>
    public IEnumerable<(MethodSignature Signature, MethodBase Builder, bool Declared)> FindMethods(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _methods.Where(m => m.Signature.Name == name);
    }

    /// <summary>
    /// The forward references never declared.
    /// </summary>
    public IEnumerable<MethodSignature> Undeclared => _methods.Where(m => !m.Declared).Select(m => m.Signature);
}
