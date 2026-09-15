using System.Collections;
using System.Globalization;
using IlRepl.Protocol;

namespace IlRepl.Engine;

/// <summary>
/// Observes collection contents and comparer settings without reading randomized hash storage or invoking user callbacks.
/// </summary>
internal sealed partial class StructuralObservation
{
    private ObservedValue? CaptureCollection(object value, int depth, int identity)
    {
        if (CaptureHashtable(value, depth, identity) is { } hashtable) return hashtable;
        if (CaptureConcurrentCollection(value, depth, identity) is { } concurrent) return concurrent;
        var type = value.GetType();
        var collection = type;
        while (collection is not null && (!collection.IsConstructedGenericType
            || collection.GetGenericTypeDefinition() != typeof(Dictionary<,>)
                && collection.GetGenericTypeDefinition() != typeof(HashSet<>)))
        {
            collection = collection.BaseType;
        }

        if (collection is null) return null;
        var dictionary = collection.GetGenericTypeDefinition() == typeof(Dictionary<,>);
        var name = TypeName(type);
        var members = Fields(value, depth, exceptionDetails: false, stopBefore: collection);
        var comparer = collection.GetProperty("Comparer")!.GetValue(value);
        members.Add(new ObservedMember("comparer", Capture(comparer, depth + 1)));
        // These methods belong to the framework's concrete base, so subclass interface implementations cannot run.
        var iterator = (IEnumerator)collection.GetMethod("GetEnumerator", Type.EmptyTypes)!.Invoke(value, null)!;
        try
        {
            var index = 0;
            while (iterator.MoveNext())
            {
                if (_nodes >= MaximumNodes)
                {
                    members.Add(new ObservedMember("remaining", Unavailable(name, "collection exceeds the observation limit")));
                    break;
                }

                ObservedValue item;
                if (dictionary)
                {
                    _nodes++;
                    var entry = ((IDictionaryEnumerator)iterator).Entry;
                    item = new ObservedValue("entry", "", null, null,
                        [new ObservedMember("key", Capture(entry.Key, depth + 2)),
                            new ObservedMember("value", Capture(entry.Value, depth + 2))]);
                }
                else item = Capture(iterator.Current, depth + 1);

                members.Add(new ObservedMember((index++).ToString(CultureInfo.InvariantCulture), item));
            }
        }
        catch (InvalidOperationException)
        {
            members.Add(new ObservedMember("remaining", Unavailable(name, "collection changed during observation")));
        }
        finally
        {
            (iterator as IDisposable)?.Dispose();
        }

        return new ObservedValue(dictionary ? "dictionary" : "set", name, null, identity, members);
    }

    private static ObservedValue? CaptureStringComparer(object value, string name, int identity)
    {
        if (value.GetType().Assembly != typeof(StringComparer).Assembly || value is not IEqualityComparer<string?> comparer) return null;
        if (StringComparer.IsWellKnownOrdinalComparer(comparer, out var ignoreCase))
        {
            return new ObservedValue("comparer", name, ignoreCase ? "ordinal-ignore-case" : "ordinal", identity, []);
        }

        if (StringComparer.IsWellKnownCultureAwareComparer(comparer, out var culture, out var options))
        {
            return new ObservedValue("comparer", name, culture.Name + ":" + ((int)options).ToString(CultureInfo.InvariantCulture),
                identity, []);
        }

        return null;
    }
}
