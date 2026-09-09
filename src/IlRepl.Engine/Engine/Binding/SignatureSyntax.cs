namespace IlRepl.Engine.Binding;

/// <summary>
/// A standalone signature as written: the part of a function pointer type after <c>method</c>,
/// or the operand of <c>calli</c>. <c>[instance] [explicit] [vararg] RetType(Params)</c> for a
/// managed signature, <c>unmanaged [cdecl|stdcall|thiscall|fastcall] RetType(Params)</c> for a native one.
/// </summary>
/// <param name="ConventionWords">The calling convention words as written, in order.</param>
/// <param name="ReturnType">The return type.</param>
/// <param name="Parameters">The parameter types, the vararg sentinel excluded.</param>
/// <param name="SentinelIndex">The index in <paramref name="Parameters"/> before which <c>...</c> was written, or null.</param>
public sealed record SignatureSyntax(IReadOnlyList<string> ConventionWords, TypeSyntax ReturnType, IReadOnlyList<TypeSyntax> Parameters, int? SentinelIndex);
