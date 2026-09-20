using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using IlRepl.Protocol;

namespace IlRepl.Engine;

/// <summary>
/// Captures concurrent dictionary entries without randomized storage or user collection callbacks.
/// </summary>
internal sealed partial class StructuralObservation
{
    private ObservedValue? CaptureConcurrentCollection(object value, int depth, int identity)
    {
        var type = value.GetType();
        var collection = type;
        while (collection is not null && (!collection.IsConstructedGenericType
            || collection.GetGenericTypeDefinition() != typeof(ConcurrentDictionary<,>)))
        {
            collection = collection.BaseType;
        }

        if (collection is null)
        {
            return null;
        }

        var name = TypeName(type);
        var members = Fields(value, depth, exceptionDetails: false, stopBefore: collection);
        members.Add(new ObservedMember("comparer", Capture(collection.GetProperty("Comparer")!.GetValue(value), depth + 1)));
        ObservedValue Result() => new("dictionary", name, null, identity, members);
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

        var snapshot = new DictionaryEntry[count];
        var map = collection.GetInterfaceMap(typeof(ICollection));
        var copy = Array.FindIndex(map.InterfaceMethods, method => method.Name == nameof(ICollection.CopyTo));
        try
        {
            // The BCL target holds its own locks and bypasses subclass interface implementations.
            map.TargetMethods[copy].Invoke(value, [snapshot, 0]);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is ArgumentException)
        {
            return Incomplete("collection changed during observation");
        }

        var entries = new List<(string Order, object Key, object? Value)>(count);
        // A concurrent shrink leaves unused slots; ConcurrentDictionary never accepts a null key.
        foreach (var entry in snapshot.Where(entry => entry.Key is not null))
        {
            entries.Add(("", entry.Key, entry.Value));
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

            _nodes++;
            var item = new ObservedValue("entry", "", null, null,
                [new ObservedMember("key", Capture(entry.Key, depth + 2)),
                    new ObservedMember("value", Capture(entry.Value, depth + 2))]);
            members.Add(new ObservedMember(index.ToString(CultureInfo.InvariantCulture), item));
        }

        return Result();
    }
}
