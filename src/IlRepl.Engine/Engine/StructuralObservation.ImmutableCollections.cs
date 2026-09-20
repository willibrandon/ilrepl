using System.Collections;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using IlRepl.Protocol;

namespace IlRepl.Engine;

/// <summary>
/// Orders immutable hash collections by captured keys without inspecting randomized trees or calling user comparers.
/// </summary>
internal sealed partial class StructuralObservation
{
    private ObservedValue? CaptureImmutableCollection(object value, int depth, int identity)
    {
        var type = value.GetType();
        if (!type.IsConstructedGenericType)
        {
            return null;
        }

        var definition = type.GetGenericTypeDefinition();
        var dictionary = definition == typeof(ImmutableDictionary<,>) || definition == typeof(ImmutableDictionary<,>.Builder);
        if (!dictionary && definition != typeof(ImmutableHashSet<>) && definition != typeof(ImmutableHashSet<>.Builder))
        {
            return null;
        }

        var name = TypeName(type);
        var members = new List<ObservedMember>
        {
            new("comparer", Capture(type.GetProperty("KeyComparer")!.GetValue(value), depth + 1)),
        };

        if (dictionary)
        {
            members.Add(new ObservedMember("value comparer", Capture(type.GetProperty("ValueComparer")!.GetValue(value),
                depth + 1)));
        }

        ObservedValue Result() => new(dictionary ? "dictionary" : "set", name, null, identity, members);
        ObservedValue Incomplete(string reason)
        {
            members.Add(new ObservedMember("remaining", Unavailable(name, reason)));
            return Result();
        }

        var entries = new List<(string Order, object? Key, object? Value)>();
        var iterator = dictionary ? ((IDictionary)value).GetEnumerator() : ((IEnumerable)value).GetEnumerator();
        using var iteratorLifetime = iterator as IDisposable;
        try
        {
            while (iterator.MoveNext())
            {
                if (entries.Count >= MaximumNodes - _nodes)
                {
                    return Incomplete("collection exceeds the observation limit");
                }

                if (dictionary)
                {
                    var entry = ((IDictionaryEnumerator)iterator).Entry;
                    entries.Add(("", entry.Key, entry.Value));
                }
                else
                {
                    entries.Add(("", iterator.Current, null));
                }
            }
        }
        catch (InvalidOperationException)
        {
            return Incomplete("collection changed during observation");
        }

        if (entries.Count > 1)
        {
            for (var index = 0; index < entries.Count; index++)
            {
                var entry = entries[index];
                var order = CollectionOrder(entry.Key, depth + 1);
                if (order is null)
                {
                    return Incomplete("collection keys cannot be ordered within the observation limit");
                }

                entries[index] = (order, entry.Key, entry.Value);
            }

            entries.Sort((left, right) => string.CompareOrdinal(left.Order, right.Order));
            for (var index = 1; index < entries.Count; index++)
            {
                if (entries[index - 1].Order == entries[index].Order)
                {
                    return Incomplete("distinct collection keys have indistinguishable structural order");
                }
            }
        }

        foreach (var (entry, index) in entries.Select((entry, index) => (entry, index)))
        {
            if (_nodes >= MaximumNodes)
            {
                return Incomplete("collection exceeds the observation limit");
            }

            ObservedValue item;
            if (dictionary)
            {
                _nodes++;
                item = new ObservedValue("entry", "", null, null,
                    [new ObservedMember("key", Capture(entry.Key, depth + 2)),
                        new ObservedMember("value", Capture(entry.Value, depth + 2))]);
            }
            else
            {
                item = Capture(entry.Key, depth + 1);
            }

            members.Add(new ObservedMember(index.ToString(CultureInfo.InvariantCulture), item));
        }

        return Result();
    }

    private string? CollectionOrder(object? key, int depth)
    {
        var owner = _orderingOwner ?? this;
        var observer = new StructuralObservation(typeNames, new ObservationIdentityMap(Identities))
            { _orderingOwner = owner, _nodes = _nodes };
        foreach (var pair in _identities)
        {
            observer._identities.Add(pair.Key, pair.Value);
        }

        var observation = observer.Capture(key, depth);
        var text = new StringBuilder();
        if (!AppendOrder(observation, text, 1_048_576 - owner._orderingCharacters))
        {
            return null;
        }

        owner._orderingCharacters += text.Length;
        return text.ToString();
    }

    private static bool AppendOrder(ObservedValue value, StringBuilder text, int limit)
    {
        if (value.Kind == "unavailable")
        {
            return false;
        }

        if (!Append(value.Kind) || !Append(value.Type) || !Append(value.Value)
            || !Append(value.Identity?.ToString(CultureInfo.InvariantCulture))
            || !Append(value.Members.Count.ToString(CultureInfo.InvariantCulture)))
        {
            return false;
        }

        foreach (var member in value.Members)
        {
            if (!Append(member.Name) || !AppendOrder(member.Value, text, limit))
            {
                return false;
            }
        }

        return true;

        bool Append(string? part)
        {
            if (text.Length + (part?.Length ?? 0) + 12 > limit)
            {
                return false;
            }

            text.Append((part?.Length ?? -1).ToString(CultureInfo.InvariantCulture)).Append(':').Append(part);
            return true;
        }
    }
}
