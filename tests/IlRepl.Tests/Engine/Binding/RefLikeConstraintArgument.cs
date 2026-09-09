namespace IlRepl.Tests.Engine.Binding;

/// <summary>
/// Supplies a parameter that may stand for a byref-like type when another generic definition is constructed.
/// </summary>
/// <typeparam name="T">The parameter that permits byref-like arguments.</typeparam>
public sealed class RefLikeConstraintArgument<T> where T : allows ref struct;
