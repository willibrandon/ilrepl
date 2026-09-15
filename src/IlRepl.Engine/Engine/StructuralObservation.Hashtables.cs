using System.Collections;
using System.Globalization;
using System.Reflection;
using IlRepl.Protocol;

namespace IlRepl.Engine;

/// <summary>
/// Captures hashtable entries and synchronized backing identities without virtual collection callbacks or randomized hashes.
/// </summary>
internal sealed partial class StructuralObservation
{
    private const BindingFlags HashtableFlags = BindingFlags.Instance | BindingFlags.NonPublic;

    private ObservedValue? CaptureHashtable(object value, int depth, int identity)
    {
        var type = value.GetType();
        var name = TypeName(type);
        if (CaptureLegacyComparer(value, type, name, depth, identity) is { } comparer) return comparer;
        if (value is not Hashtable table) return null;
        var synchronized = typeof(Hashtable).GetNestedType("SyncHashtable", BindingFlags.NonPublic)!;
        if (type == synchronized)
        {
            var backing = synchronized.GetField("_table", HashtableFlags)!.GetValue(table);
            return new ObservedValue("dictionary", name, null, identity, [new ObservedMember("table", Capture(backing, depth + 1))]);
        }

        var members = Fields(value, depth, exceptionDetails: false, stopBefore: typeof(Hashtable));
        var settings = typeof(Hashtable).GetProperty("EqualityComparer", HashtableFlags)!.GetValue(table);
        members.Add(new ObservedMember("comparer", Capture(settings, depth + 1)));
        ObservedValue Result() => new("dictionary", name, null, identity, members);
        ObservedValue Incomplete(string reason)
        {
            members.Add(new ObservedMember("remaining", Unavailable(name, reason)));
            return Result();
        }
        T State<T>(string field) => (T)typeof(Hashtable).GetField(field, HashtableFlags)!.GetValue(table)!;
        var version = State<int>("_version");
        if (State<bool>("_isWriterInProgress")) return Incomplete("collection changed during observation");
        // Reflection boxes these volatile value fields without an acquire read; order the snapshot explicitly.
        Thread.MemoryBarrier();
        var count = State<int>("_count");
        if (count > MaximumNodes - _nodes) return Incomplete("collection exceeds the observation limit");
        var snapshot = new DictionaryEntry[count];
        try
        {
            // CopyEntries is nonvirtual; invoking public CopyTo would dispatch user Count or CopyTo overrides.
            typeof(Hashtable).GetMethod("CopyEntries", HashtableFlags)!.Invoke(table, [snapshot, 0]);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is ArgumentException or IndexOutOfRangeException)
        {
            return Incomplete("collection changed during observation");
        }
        Thread.MemoryBarrier();
        if (State<bool>("_isWriterInProgress") || version != State<int>("_version") || count != State<int>("_count"))
            return Incomplete("collection changed during observation");

        var entries = new List<(string Order, object Key, object? Value)>(count);
        foreach (var entry in snapshot)
        {
            if (entry.Key is null) return Incomplete("collection changed during observation");
            entries.Add(("", entry.Key, entry.Value));
        }
        if (entries.Count > 1)
        {
            for (var index = 0; index < entries.Count; index++)
            {
                var entry = entries[index];
                var order = CollectionOrder(entry.Key, depth + 1);
                if (order is null) return Incomplete("collection keys cannot be ordered within the observation limit");
                entries[index] = (order, entry.Key, entry.Value);
            }
            entries.Sort((left, right) => string.CompareOrdinal(left.Order, right.Order));
            for (var index = 1; index < entries.Count; index++)
                if (entries[index - 1].Order == entries[index].Order)
                    return Incomplete("distinct collection keys have indistinguishable structural order");
        }
        foreach (var (entry, index) in entries.Select((entry, index) => (entry, index)))
        {
            if (_nodes >= MaximumNodes) return Incomplete("collection exceeds the observation limit");
            _nodes++;
            var item = new ObservedValue("entry", "", null, null,
                [new ObservedMember("key", Capture(entry.Key, depth + 2)),
                    new ObservedMember("value", Capture(entry.Value, depth + 2))]);
            members.Add(new ObservedMember(index.ToString(CultureInfo.InvariantCulture), item));
        }
        return Result();
    }

    private ObservedValue? CaptureLegacyComparer(object value, Type type, string name, int depth, int identity)
    {
        var provider = typeof(CaseInsensitiveComparer).Assembly.GetType("System.Collections.CaseInsensitiveHashCodeProvider");
        var parent = type;
        while (parent is not null && parent != typeof(CaseInsensitiveComparer) && parent != typeof(Comparer) && parent != provider)
            parent = parent.BaseType;
        if (parent is null) return null;
        var info = (CompareInfo)parent.GetField("_compareInfo", HashtableFlags)!.GetValue(value)!;
        var options = parent == typeof(Comparer) ? CompareOptions.None : CompareOptions.IgnoreCase;
        return new ObservedValue("comparer", name, info.Name + ":" + ((int)options).ToString(CultureInfo.InvariantCulture),
            identity, Fields(value, depth, exceptionDetails: false, stopBefore: parent));
    }
}
