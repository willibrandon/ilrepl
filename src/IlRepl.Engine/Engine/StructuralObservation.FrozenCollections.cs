using System.Collections.Frozen;
using System.Globalization;
using System.Reflection;
using IlRepl.Protocol;

namespace IlRepl.Engine;

/// <summary>
/// Captures frozen collection contents and comparers without randomized storage or user callbacks.
/// </summary>
internal sealed partial class StructuralObservation
{
    private ObservedValue? CaptureFrozenCollection(object value, int depth, int identity)
    {
        var type = value.GetType();
        var collection = FrozenCollectionBase(type);
        if (collection is null)
        {
            return null;
        }

        var name = TypeName(collection);
        // The private-protected constructors limit legitimate derived implementations to the framework assembly.
        if (type.Assembly != collection.Assembly)
        {
            return Unavailable(name, "custom frozen collection implementations cannot be inspected without executing user code");
        }

        var dictionary = collection.GetGenericTypeDefinition() == typeof(FrozenDictionary<,>);
        var members = new List<ObservedMember>
        {
            new("comparer", Capture(collection.GetProperty("Comparer")!.GetValue(value), depth + 1)),
        };
        ObservedValue Result() => new(dictionary ? "dictionary" : "set", name, null, identity, members);
        ObservedValue Incomplete(string reason)
        {
            members.Add(new ObservedMember("remaining", Unavailable(name, reason)));
            return Result();
        }

        var count = (int)collection.GetProperty("Count")!.GetValue(value)!;
        if (count > MaximumNodes - _nodes)
        {
            return Incomplete("collection exceeds the observation limit");
        }

        Array Items(string property) => (Array)collection.GetProperty(property, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(value)!;
        // BCL core getters expose paired arrays; no user enumerator, comparer, or virtual collection override can run.
        var keys = Items(dictionary ? "KeysCore" : "ItemsCore");
        var values = dictionary ? Items("ValuesCore") : null;
        var entries = new List<(string Order, object? Key, object? Value)>(count);
        for (var index = 0; index < count; index++)
        {
            entries.Add(("", keys.GetValue(index), values?.GetValue(index)));
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

    private static Type? FrozenCollectionBase(Type? type)
    {
        while (type is not null && (!type.IsConstructedGenericType
            || type.GetGenericTypeDefinition() != typeof(FrozenDictionary<,>)
                && type.GetGenericTypeDefinition() != typeof(FrozenSet<>)))
        {
            type = type.BaseType;
        }

        return type;
    }
}
