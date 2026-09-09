using System.Reflection;

namespace IlRepl.Engine.Binding;

/// <summary>
/// A field of a loaded generic definition mapped through a construction during runtime emission.
/// </summary>
/// <param name="Field">The field on the definition.</param>
internal sealed record RuntimeDefinitionField(FieldInfo Field);
