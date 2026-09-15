using System.Collections;
using System.Globalization;
using System.Reflection;
using IlRepl.Protocol;

namespace IlRepl.Engine;

/// <summary>
/// Observes LINQ lookups by comparer, key, and element order without retaining randomized hash storage.
/// </summary>
internal sealed partial class StructuralObservation
{
    private ObservedValue? CaptureLookup(object value, int depth, int identity)
    {
        var type = value.GetType();
        if (!ImplementsLookup(type)) return null;
        var name = TypeName(type);
        var lookup = FrameworkLookup(type);
        if (lookup is null)
        {
            return Unavailable(name, "lookup implementation cannot be observed without invoking user code");
        }

        var comparer = lookup.GetField("_comparer", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(value);
        if (comparer is null) return Unavailable(name, "runtime lookup comparer is unavailable");
        var members = new List<ObservedMember> { new("comparer", Capture(comparer, depth + 1)) };
        var iterator = (IEnumerator)lookup.GetMethod(nameof(IEnumerable.GetEnumerator), Type.EmptyTypes)!.Invoke(value, null)!;
        try
        {
            var index = 0;
            while (iterator.MoveNext())
            {
                if (_nodes >= MaximumNodes)
                {
                    members.Add(new ObservedMember("remaining", Unavailable(name, "lookup exceeds the observation limit")));
                    break;
                }

                var grouping = iterator.Current!;
                var groupMembers = new List<ObservedMember>
                {
                    new("key", Capture(grouping.GetType().GetProperty("Key")!.GetValue(grouping), depth + 2)),
                };
                var elements = ((IEnumerable)grouping).GetEnumerator();
                try
                {
                    var element = 0;
                    while (elements.MoveNext())
                    {
                        if (_nodes >= MaximumNodes)
                        {
                            groupMembers.Add(new ObservedMember("remaining",
                                Unavailable(name, "lookup exceeds the observation limit")));
                            break;
                        }

                        groupMembers.Add(new ObservedMember(element.ToString(CultureInfo.InvariantCulture),
                            Capture(elements.Current, depth + 2)));
                        element++;
                    }
                }
                finally
                {
                    (elements as IDisposable)?.Dispose();
                }

                _nodes++;
                members.Add(new ObservedMember(index.ToString(CultureInfo.InvariantCulture),
                    new ObservedValue("group", "", null, null, groupMembers)));
                index++;
            }
        }
        finally
        {
            (iterator as IDisposable)?.Dispose();
        }

        return new ObservedValue("lookup", name, null, identity, members);
    }

    private static bool ImplementsLookup(Type type) => type.GetInterfaces().Any(candidate => candidate.IsConstructedGenericType
        && candidate.GetGenericTypeDefinition() == typeof(ILookup<,>));

    private static Type? FrameworkLookup(Type type)
    {
        if (type.Assembly != typeof(Enumerable).Assembly) return null;
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (current.IsConstructedGenericType && current.GetGenericTypeDefinition().FullName == "System.Linq.Lookup`2")
                return current;
        }

        return null;
    }
}
