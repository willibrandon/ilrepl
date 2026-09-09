namespace IlRepl.Engine.Binding;

/// <summary>
/// Plans the replacement of a definition other definitions depend on: the closure of families and
/// methods that must be rebuilt with it, and the order they replay in. The walk is over whatever
/// stands for a type's identity, a runtime type for the live session or a symbol for a preview,
/// so both compute the same closure from the same mentions.
/// </summary>
public static class DefinitionReplacementPlanner
{
    /// <summary>
    /// Computes the closure of a replacement: starting from the identities the replaced family
    /// defines, every family whose declarations or bodies mention a collected identity or method
    /// joins, and so does every method whose signature or body does, until nothing new joins.
    /// </summary>
    /// <typeparam name="TFamily">A type family record.</typeparam>
    /// <typeparam name="TMethod">A session method record.</typeparam>
    /// <typeparam name="TType">What stands for a type's identity.</typeparam>
    /// <param name="replaced">The family being replaced.</param>
    /// <param name="families">Every family the session holds, the replaced one included.</param>
    /// <param name="methods">Every method the session holds.</param>
    /// <param name="identitiesOf">The identities a family defines, every generation bodies may be bound to.</param>
    /// <param name="familyMentions">True when a family mentions one of the identities or methods collected so far.</param>
    /// <param name="methodMentions">True when a method mentions one of the identities or methods collected so far.</param>
    /// <param name="nameOf">A method's name, which is what a call binds to.</param>
    /// <param name="comparer">How identities compare.</param>
    /// <returns>The closure, without the replaced family itself.</returns>
    public static ReplacementClosure<TFamily, TMethod> Plan<TFamily, TMethod, TType>(
        TFamily replaced,
        IReadOnlyList<TFamily> families,
        IReadOnlyList<TMethod> methods,
        Func<TFamily, IEnumerable<TType>> identitiesOf,
        Func<TFamily, IReadOnlySet<TType>, IReadOnlySet<string>, bool> familyMentions,
        Func<TMethod, IReadOnlySet<TType>, IReadOnlySet<string>, bool> methodMentions,
        Func<TMethod, string> nameOf,
        IEqualityComparer<TType> comparer)
        where TFamily : class
        where TMethod : class
        where TType : notnull
    {
        ArgumentNullException.ThrowIfNull(replaced);
        ArgumentNullException.ThrowIfNull(families);
        ArgumentNullException.ThrowIfNull(methods);
        ArgumentNullException.ThrowIfNull(identitiesOf);
        ArgumentNullException.ThrowIfNull(familyMentions);
        ArgumentNullException.ThrowIfNull(methodMentions);
        ArgumentNullException.ThrowIfNull(nameOf);
        ArgumentNullException.ThrowIfNull(comparer);
        var dependentFamilies = new List<TFamily>();
        var dependentMethods = new List<TMethod>();
        var mentionedTypes = new HashSet<TType>(identitiesOf(replaced), comparer);
        var mentionedMethods = new HashSet<string>(StringComparer.Ordinal);
        bool changed;
        do
        {
            changed = false;
            foreach (var family in families)
            {
                if (ReferenceEquals(family, replaced) || dependentFamilies.Contains(family))
                {
                    continue;
                }

                if (familyMentions(family, mentionedTypes, mentionedMethods))
                {
                    dependentFamilies.Add(family);
                    foreach (var identity in identitiesOf(family))
                    {
                        mentionedTypes.Add(identity);
                    }

                    changed = true;
                }
            }

            foreach (var method in methods)
            {
                if (dependentMethods.Contains(method))
                {
                    continue;
                }

                if (methodMentions(method, mentionedTypes, mentionedMethods))
                {
                    dependentMethods.Add(method);
                    mentionedMethods.Add(nameOf(method));
                    changed = true;
                }
            }
        }
        while (changed);

        return new ReplacementClosure<TFamily, TMethod>(dependentFamilies, dependentMethods);
    }

    /// <summary>
    /// The order a group replays in: every member in the order it was accepted, the edited block
    /// last, so each replay binds to what was declared before it.
    /// </summary>
    /// <typeparam name="T">A replay descriptor.</typeparam>
    /// <param name="members">The members with their acceptance order; the edited block carries <see cref="int.MaxValue"/>.</param>
    /// <returns>The members in replay order.</returns>
    public static IReadOnlyList<T> ReplayOrder<T>(IEnumerable<(int Order, T Member)> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        return [.. members.OrderBy(m => m.Order).Select(m => m.Member)];
    }
}
