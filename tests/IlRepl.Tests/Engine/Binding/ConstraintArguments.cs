namespace IlRepl.Tests.Engine.Binding;

/// <summary>
/// Supplies primary-class and flag-only generic parameter chains for independent CLR constraint comparisons.
/// </summary>
/// <typeparam name="TStream">A parameter constrained by a concrete reference base.</typeparam>
/// <typeparam name="TDerived">A parameter inheriting that primary class constraint.</typeparam>
/// <typeparam name="TReference">A parameter with only the class special constraint.</typeparam>
/// <typeparam name="TFlagOnly">A parameter referring to the flag-only parameter.</typeparam>
internal sealed class ConstraintArguments<TStream, TDerived, TReference, TFlagOnly>
    where TStream : Stream
    where TDerived : TStream
    where TReference : class
    where TFlagOnly : TReference
{
}
