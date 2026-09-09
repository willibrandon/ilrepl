using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace IlRepl.Engine.Binding;

/// <summary>
/// Records loaded-context identity and known binding rules without retaining the context itself.
/// </summary>
internal sealed record LoadedContextIdentity(long Id, bool IsDefault, bool IsSession, bool UsesDefaultFallback)
{
    private static readonly ConditionalWeakTable<AssemblyLoadContext, LoadedContextIdentity> Identities = [];
    private static long s_nextId;

    /// <summary>
    /// Copies the binding facts of a context without invoking its Load override or resolution handlers.
    /// </summary>
    /// <param name="context">The loaded assembly's context.</param>
    /// <returns>The context identity and deterministic fallback rules.</returns>
    public static LoadedContextIdentity Of(AssemblyLoadContext? context) => context is null
        ? new(0, false, false, false)
        : Identities.GetValue(context, current => new(Interlocked.Increment(ref s_nextId),
            ReferenceEquals(current, AssemblyLoadContext.Default), current is DefinitionLoadContext,
            current.GetType() == typeof(AssemblyLoadContext) || current is DefinitionLoadContext
                || ReferenceEquals(current, AssemblyLoadContext.Default)));
}
