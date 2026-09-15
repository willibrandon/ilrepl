using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using IlRepl.Protocol;

namespace IlRepl.Engine;

/// <summary>
/// A captured original and the last successfully committed session-owned version of a method.
/// </summary>
public sealed class MethodEdit
{
    internal MethodEdit(string name, string reference, ImportedMethodFamily baseline)
    {
        Name = name;
        Reference = reference;
        Baseline = baseline;
        Source = baseline.Selected.Source;
        Fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            baseline.Selected.Method.Module.ModuleVersionId + ":" + baseline.Selected.Method.MetadataToken + ":" + Source)));
    }

    /// <summary>
    /// The stable name chosen for the copy.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// The original method reference used to create this edit.
    /// </summary>
    public string Reference { get; }

    /// <summary>
    /// The immutable original disassembly.
    /// </summary>
    public DisassembledMethod Original => Baseline.Selected.Listing;

    /// <summary>
    /// The captured executable with pinned helper bindings and independent declaring context.
    /// </summary>
    public MethodBase OriginalMethod => Baseline.EntryPoint
        ?? throw new ReplException("the original context cannot be reproduced: " + string.Join(Environment.NewLine, Baseline.Problems));

    /// <summary>
    /// The replayable method definition of the last successful revision, or initial draft.
    /// </summary>
    public string Source { get; internal set; }

    /// <summary>
    /// The compiled copy, or null until the first successful commit.
    /// </summary>
    public MethodBase? Method => Current?.EntryPoint;

    /// <summary>
    /// The identity of the captured original, retained across revisions.
    /// </summary>
    public string Fingerprint { get; }

    /// <summary>
    /// The number of successful commits to this copy.
    /// </summary>
    public int Revision { get; internal set; }

    /// <summary>
    /// The exact symbols reached while preparing the current revision.
    /// </summary>
    public IReadOnlyList<EditDependency> Dependencies => (Current ?? Baseline).Dependencies;

    /// <summary>
    /// The current draft's preflight blockers; an edited body can remove dependencies that caused them.
    /// </summary>
    public IReadOnlyList<string> Problems => (Current ?? Baseline).Problems;

    /// <summary>
    /// The immutable executable family captured when the edit was first prepared.
    /// </summary>
    internal ImportedMethodFamily Baseline { get; }

    /// <summary>
    /// The last successfully published family, or null before the first commit.
    /// </summary>
    internal ImportedMethodFamily? Current { get; set; }

    /// <summary>
    /// Requires compatible original and edited call signatures before exporting a shared scenario.
    /// </summary>
    /// <exception cref="ReplException">The scenario cannot call both versions with the same signature.</exception>
    internal void RequireScenarioSignature()
    {
        var original = Original.Requested as MethodInfo;
        var edited = Method as MethodInfo;
        var before = original?.GetParameters();
        var after = edited?.GetParameters();
        if (original is null || edited is null || original.CallingConvention != edited.CallingConvention
            || before!.Length != after!.Length || !SameParameter(original.ReturnParameter, edited.ReturnParameter)
            || before.Where((parameter, index) => !SameParameter(parameter, after[index])).Any()
            || !SameGenerics(original, edited))
        {
            throw new ReplException("the original and edited signatures must match to compare this method through a scenario");
        }

        bool SameType(Type first, Type second) => TypeKey(first) == Current!.NormalizeNames(TypeKey(second));

        static string TypeKey(Type type) => IlSignatureRenderer.IlAsm(IlSignature.FromType(type));

        bool SameModifiers(Type[] first, Type[] second) => first.Length == second.Length
            && first.Zip(second).All(pair => SameType(pair.First, pair.Second));

        bool SameParameter(ParameterInfo first, ParameterInfo second) => SameType(first.ParameterType, second.ParameterType)
            && SameModifiers(first.GetRequiredCustomModifiers(), second.GetRequiredCustomModifiers())
            && SameModifiers(first.GetOptionalCustomModifiers(), second.GetOptionalCustomModifiers());

        bool SameGenerics(MethodInfo first, MethodInfo second)
        {
            var firstParameters = Parameters(first);
            var secondParameters = Parameters(second);
            return firstParameters.Length == secondParameters.Length && firstParameters.Zip(secondParameters).All(pair =>
                pair.First.GenericParameterAttributes == pair.Second.GenericParameterAttributes
                && pair.First.GetGenericParameterConstraints().Select(TypeKey).ToHashSet(StringComparer.Ordinal)
                    .SetEquals(pair.Second.GetGenericParameterConstraints().Select(type =>
                        Current!.NormalizeNames(TypeKey(type)))));
        }

        static Type[] Parameters(MethodInfo method) =>
            (method.DeclaringType is { IsGenericType: true } owner
                ? owner.GetGenericTypeDefinition().GetGenericArguments() : Type.EmptyTypes)
            .Concat(method.IsGenericMethod ? method.GetGenericMethodDefinition().GetGenericArguments() : Type.EmptyTypes).ToArray();
    }
}
