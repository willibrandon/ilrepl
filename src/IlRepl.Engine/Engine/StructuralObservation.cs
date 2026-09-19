using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using IlRepl.Protocol;

namespace IlRepl.Engine;

/// <summary>
/// Observes instance fields without executing user getters, formatting, equality, or constructors.
/// </summary>
internal sealed partial class StructuralObservation(
    IReadOnlyDictionary<string, string> typeNames,
    ObservationIdentityMap? identities = null)
{
    private const int MaximumNodes = 4096;
    private const int MaximumDepth = 64;
    private readonly Dictionary<object, int> _identities = new(ReferenceEqualityComparer.Instance);
    private int _nodes;
    private StructuralObservation? _orderingOwner;
    private int _orderingNodes;
    private int _orderingCharacters;

    /// <summary>
    /// The weak reference identities that a later snapshot can reuse while capturing all fields again.
    /// </summary>
    internal ObservationIdentityMap Identities { get; } = identities ?? new ObservationIdentityMap();

    /// <summary>
    /// Captures one value using the shared object-identity map for all roots in this observation.
    /// </summary>
    /// <param name="value">The value to inspect.</param>
    /// <returns>The structural value, or an explicit unavailable observation.</returns>
    internal ObservedValue Capture(object? value) => Capture(value, 0);

    private ObservedValue Capture(object? value, int depth)
    {
        if (_orderingOwner is { } ordering && ++ordering._orderingNodes > MaximumNodes)
        {
            return Unavailable(value is null ? "" : TypeName(value.GetType()), "collection ordering exceeds the observation limit");
        }

        if (++_nodes > MaximumNodes || depth > MaximumDepth)
        {
            return Unavailable(value is null ? "" : TypeName(value.GetType()), "structural observation exceeded its node or depth limit");
        }

        if (value is null)
        {
            return new ObservedValue("null", "", null, null, []);
        }

        if (value is NullReferenceObservation)
        {
            return new ObservedValue("null-reference", "", null, null, []);
        }

        if (value is NullTaskObservation)
        {
            return new ObservedValue("null-task", typeof(Task).FullName!, null, null, []);
        }

        var type = value.GetType();
        var name = TypeName(type);
        if (value is UnavailableObservation unavailable)
        {
            return Unavailable(name, unavailable.Reason);
        }

        var scalar = value switch
        {
            bool boolean => boolean ? "true" : "false",
            char character => ((int)character).ToString("x4", CultureInfo.InvariantCulture),
            byte number => number.ToString(CultureInfo.InvariantCulture),
            sbyte number => number.ToString(CultureInfo.InvariantCulture),
            short number => number.ToString(CultureInfo.InvariantCulture),
            ushort number => number.ToString(CultureInfo.InvariantCulture),
            int number => number.ToString(CultureInfo.InvariantCulture),
            uint number => number.ToString(CultureInfo.InvariantCulture),
            long number => number.ToString(CultureInfo.InvariantCulture),
            ulong number => number.ToString(CultureInfo.InvariantCulture),
            float number => BitConverter.SingleToInt32Bits(number).ToString("x8", CultureInfo.InvariantCulture),
            double number => BitConverter.DoubleToInt64Bits(number).ToString("x16", CultureInfo.InvariantCulture),
            Half number => BitConverter.HalfToInt16Bits(number).ToString("x4", CultureInfo.InvariantCulture),
            decimal number => string.Join(":", decimal.GetBits(number).Select(part => part.ToString("x8", CultureInfo.InvariantCulture))),
            string text when text.Length <= 65536 => text,
            DateTime time => time.Ticks.ToString(CultureInfo.InvariantCulture) + ":" + (int)time.Kind,
            DateTimeOffset time => time.Ticks.ToString(CultureInfo.InvariantCulture) + ":"
                + time.Offset.Ticks.ToString(CultureInfo.InvariantCulture),
            TimeSpan time => time.Ticks.ToString(CultureInfo.InvariantCulture),
            Guid guid => Convert.ToHexString(guid.ToByteArray()),
            Type reflected => TypeName(reflected),
            _ => null,
        };
        if (scalar is not null)
        {
            return Scalar(value, name, scalar);
        }

        if (value is string)
        {
            return Unavailable(name, "string exceeds the observation limit");
        }

        if (value is IntPtr or UIntPtr or SafeHandle or Delegate || type.IsPointer || type.IsByRefLike)
        {
            return Unavailable(name, "return an explicit scenario observation for handles, pointers, delegates, or byref-like values");
        }

        if (type.IsEnum)
        {
            var underlying = Convert.ChangeType(value, Enum.GetUnderlyingType(type), CultureInfo.InvariantCulture);
            return Scalar(value, name, Convert.ToString(underlying, CultureInfo.InvariantCulture));
        }

        if (_identities.TryGetValue(value, out var seen))
        {
            name = TypeName(FrozenCollectionBase(type) ?? type);
            return new ObservedValue("reference", name, null, seen, []);
        }

        var identity = Identities.Get(value);
        _identities.Add(value, identity);

        if (CaptureStringComparer(value, name, identity) is { } comparer)
        {
            return comparer;
        }

        if (CaptureImmutableCollection(value, depth, identity) is { } immutable)
        {
            return immutable;
        }

        if (CaptureLookup(value, depth, identity) is { } lookup)
        {
            return lookup;
        }

        if (CaptureCollection(value, depth, identity) is { } collection)
        {
            return collection;
        }

        var members = new List<ObservedMember>();
        if (value is Array array)
        {
            var bounds = string.Join(",", Enumerable.Range(0, array.Rank)
                .Select(dimension => array.GetLowerBound(dimension).ToString(CultureInfo.InvariantCulture) + ":"
                    + array.GetLength(dimension).ToString(CultureInfo.InvariantCulture)));
            var index = 0;
            foreach (var item in array)
            {
                if (_nodes >= MaximumNodes)
                {
                    members.Add(new ObservedMember("remaining", Unavailable(name, "array exceeds the observation limit")));
                    break;
                }

                members.Add(new ObservedMember((index++).ToString(CultureInfo.InvariantCulture), Capture(item, depth + 1)));
            }

            return new ObservedValue("array", name, bounds, identity, members);
        }

        return new ObservedValue("object", name, null, identity, Fields(value, depth, exceptionDetails: false));
    }

    private List<ObservedMember> Fields(object value, int depth, bool exceptionDetails, Type? stopBefore = null)
    {
        var members = new List<ObservedMember>();
        var hierarchy = new Stack<Type>();
        var type = value.GetType();
        for (var parent = type; parent is not null && parent != stopBefore; parent = parent.BaseType)
        {
            hierarchy.Push(parent);
        }

        foreach (var parent in hierarchy)
        {
            foreach (var field in parent.GetFields(BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .OrderBy(field => field.MetadataToken))
            {
                if (parent == typeof(Exception))
                {
                    var storedDetail = field.Name is "_data" or "_helpURL" or "_source";
                    var commonDetail = field.Name is "_message" or "_innerException" or "_HResult";
                    if (!storedDetail && (exceptionDetails || !commonDetail))
                    {
                        continue;
                    }
                }

                if (parent == typeof(AggregateException)
                    && (field.Name == "_rocView" || exceptionDetails && field.Name == "_innerExceptions"))
                {
                    continue;
                }

                var key = TypeName(parent) + "::" + field.Name;
                if (_nodes >= MaximumNodes)
                {
                    members.Add(new ObservedMember(key, Unavailable(TypeName(type), "fields exceed the observation limit")));
                    return members;
                }

                if (field.FieldType.IsPointer || field.FieldType.IsFunctionPointer || field.FieldType.IsByRefLike)
                {
                    members.Add(new ObservedMember(key, Unavailable(TypeName(field.FieldType), "field cannot be structurally captured")));
                    continue;
                }

                try
                {
                    members.Add(new ObservedMember(key, Capture(field.GetValue(value), depth + 1)));
                }
                catch (Exception ex) when (ex is NotSupportedException or ArgumentException or MemberAccessException)
                {
                    members.Add(new ObservedMember(key, Unavailable(TypeName(field.FieldType), "runtime cannot read this field: "
                        + ex.GetType().Name)));
                }
            }
        }

        return members;
    }

    private ObservedValue Scalar(object value, string name, string? scalar)
    {
        if (!_identities.TryGetValue(value, out var identity))
        {
            identity = Identities.Get(value);
            _identities.Add(value, identity);
        }

        return new ObservedValue("scalar", name, scalar, identity, []);
    }

    private string TypeName(Type type)
    {
        if (typeNames.TryGetValue(type.FullName ?? type.Name, out var name))
        {
            return name;
        }

        if (type.HasElementType)
        {
            var suffix = type.IsArray ? type.GetArrayRank() == 1 && !type.IsSZArray ? "[*]"
                : "[" + new string(',', type.GetArrayRank() - 1) + "]" : type.IsPointer ? "*" : "&";
            return TypeName(type.GetElementType()!) + suffix;
        }

        if (type.IsConstructedGenericType)
        {
            return TypeName(type.GetGenericTypeDefinition()) + "<" + string.Join(",", type.GetGenericArguments().Select(TypeName)) + ">";
        }

        return "[" + type.Assembly.GetName().Name + "]" + (type.FullName ?? type.Name);
    }

    private static ObservedValue Unavailable(string name, string reason) => new("unavailable", name, reason, null, []);

    /// <summary>
    /// Captures stored exception data without invoking a user-defined Message override.
    /// </summary>
    /// <param name="exception">The original exception after invocation wrappers are removed.</param>
    /// <returns>The exception observation.</returns>
    internal ObservedException Exception(Exception exception)
    {
        var nodes = 0;
        return Exception(exception, new HashSet<Exception>(ReferenceEqualityComparer.Instance), ref nodes);
    }

    private ObservedException Exception(Exception exception, HashSet<Exception> seen, ref int nodes)
    {
        var field = typeof(Exception).GetField("_message", BindingFlags.Instance | BindingFlags.NonPublic);
        var type = TypeName(exception.GetType());
        if (++nodes > MaximumNodes)
        {
            return new ObservedException(type, null, exception.HResult, null)
            {
                Problem = "exception tree exceeds the observation node limit",
            };
        }

        if (seen.Count >= MaximumDepth || !seen.Add(exception))
        {
            return new ObservedException(type, null, exception.HResult, null)
            {
                Problem = "exception chain is cyclic or exceeds the observation depth limit",
            };
        }

        try
        {
            if (!_identities.TryGetValue(exception, out var identity))
            {
                identity = Identities.Get(exception);
                _identities.Add(exception, identity);
            }

            var message = field?.GetValue(exception) as string;
            var fields = Fields(exception, seen.Count - 1, exceptionDetails: true);
            var inner = exception.InnerException is { } first ? Exception(first, seen, ref nodes) : null;
            var additional = new List<ObservedException>();
            if (exception is AggregateException aggregate)
            {
                for (var index = 1; index < aggregate.InnerExceptions.Count; index++)
                {
                    additional.Add(Exception(aggregate.InnerExceptions[index], seen, ref nodes));
                    if (nodes > MaximumNodes)
                    {
                        break;
                    }
                }
            }

            return new ObservedException(type, message is { Length: > 65536 } ? message[..65536] : message, exception.HResult, inner)
            {
                Identity = identity,
                Fields = fields,
                AdditionalInnerExceptions = additional,
                Problem = field is null ? "runtime does not expose the stored exception message"
                    : message is { Length: > 65536 } ? "exception message exceeds the observation limit" : null,
            };
        }
        finally
        {
            seen.Remove(exception);
        }
    }
}
