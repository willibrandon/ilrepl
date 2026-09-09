namespace IlRepl.Engine.Binding;

/// <summary>
/// The signature part of a function pointer type, <c>method [instance] [unmanaged cdecl] RetType *(Params)</c>.
/// </summary>
/// <param name="ConventionWords">The calling convention words as written, in order.</param>
/// <param name="ReturnType">The return type.</param>
/// <param name="Parameters">The parameter types, the vararg sentinel excluded.</param>
/// <param name="SentinelIndex">The index in <paramref name="Parameters"/> before which <c>...</c> was written, or null.</param>
public sealed record FunctionPointerSyntax(IReadOnlyList<string> ConventionWords, TypeSyntax ReturnType, IReadOnlyList<TypeSyntax> Parameters, int? SentinelIndex);
