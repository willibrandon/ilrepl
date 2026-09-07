using System.Reflection;

namespace IlRepl.Engine;

/// <summary>
/// An <c>.override</c> line: the method whose slot the declaring method implements.
/// </summary>
/// <param name="Target">The base or interface method being implemented.</param>
/// <param name="Source">The line as typed.</param>
public sealed record OverrideDeclaration(MethodBase Target, string Source);

/// <summary>
/// A class-level <c>.override T::M with ...</c> line, resolved when the type closes.
/// </summary>
/// <param name="Target">The base or interface method being implemented.</param>
/// <param name="BodyName">The name of the method on this type that implements it.</param>
/// <param name="BodyReturnType">The implementing method's return type.</param>
/// <param name="BodyParameterTypes">The implementing method's parameter types.</param>
/// <param name="BodyIsStatic">True when the implementing method is static.</param>
/// <param name="Source">The line as typed.</param>
public sealed record ClassOverrideDeclaration(MethodBase Target, string BodyName, Type BodyReturnType, IReadOnlyList<Type> BodyParameterTypes, bool BodyIsStatic, string Source);
