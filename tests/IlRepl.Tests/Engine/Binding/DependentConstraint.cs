namespace IlRepl.Tests.Engine.Binding;

/// <summary>
/// Requires the second generic argument to be assignable to the first.
/// </summary>
/// <typeparam name="TBase">The target argument.</typeparam>
/// <typeparam name="TDerived">The argument constrained to the target.</typeparam>
public sealed class DependentConstraint<TBase, TDerived> where TDerived : TBase;
