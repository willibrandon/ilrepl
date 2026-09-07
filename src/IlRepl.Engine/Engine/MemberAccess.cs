using System.Reflection;

namespace IlRepl.Engine;

/// <summary>
/// The accessibility rules of ECMA-335 as the REPL teaches them, and the words ILAsm uses for
/// them. Because a consumer skips the runtime's checks for session assemblies, this is the only
/// place a session member's access is enforced.
/// </summary>
public static class MemberAccess
{
    /// <summary>
    /// The ILAsm access word for a field.
    /// </summary>
    /// <param name="attributes">The field attributes.</param>
    /// <returns>The word, for example <c>public</c> or <c>privatescope</c>.</returns>
    public static string AccessWord(FieldAttributes attributes) => (attributes & FieldAttributes.FieldAccessMask) switch
    {
        FieldAttributes.Public => "public",
        FieldAttributes.Private => "private",
        FieldAttributes.Family => "family",
        FieldAttributes.Assembly => "assembly",
        FieldAttributes.FamANDAssem => "famandassem",
        FieldAttributes.FamORAssem => "famorassem",
        _ => "privatescope",
    };

    /// <summary>
    /// The ILAsm access word for a method.
    /// </summary>
    /// <param name="attributes">The method attributes.</param>
    /// <returns>The word.</returns>
    public static string AccessWord(MethodAttributes attributes) => (attributes & MethodAttributes.MemberAccessMask) switch
    {
        MethodAttributes.Public => "public",
        MethodAttributes.Private => "private",
        MethodAttributes.Family => "family",
        MethodAttributes.Assembly => "assembly",
        MethodAttributes.FamANDAssem => "famandassem",
        MethodAttributes.FamORAssem => "famorassem",
        _ => "privatescope",
    };

    /// <summary>
    /// The ILAsm visibility words for a type.
    /// </summary>
    /// <param name="attributes">The type attributes.</param>
    /// <returns>The words, for example <c>public</c> or <c>nested family</c>.</returns>
    public static string VisibilityWord(TypeAttributes attributes) => (attributes & TypeAttributes.VisibilityMask) switch
    {
        TypeAttributes.Public => "public",
        TypeAttributes.NestedPublic => "nested public",
        TypeAttributes.NestedPrivate => "nested private",
        TypeAttributes.NestedFamily => "nested family",
        TypeAttributes.NestedAssembly => "nested assembly",
        TypeAttributes.NestedFamANDAssem => "nested famandassem",
        TypeAttributes.NestedFamORAssem => "nested famorassem",
        _ => "private",
    };
}
