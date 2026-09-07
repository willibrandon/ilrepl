using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using IlRepl.Engine;
using IlRepl.Protocol;

namespace IlRepl.Repl;

/// <summary>
/// Formats the value a cell returned as styled spans: strings quoted, numbers as numbers,
/// arrays and collections expanded, an instance of a session type by its fields, everything
/// else through <c>ToString</c>. A session instance is shown structurally, base fields first,
/// through generated readers that run none of its code, unless the type overrides
/// <c>ToString</c> itself, in which case that override speaks for it.
/// </summary>
public static class ValueFormatter
{
    private const int MaxItems = 32;
    private const int MaxDepth = 3;
    private const int MaxFields = 16;
    private const int MaxLength = 512;
    private static readonly ConditionalWeakTable<Type, StrongBox<bool>> OverridesToString = [];
    private static readonly ConditionalWeakTable<Type, FieldReader[]> Readers = [];

    /// <summary>
    /// Formats a value followed by its runtime type.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <returns>The spans.</returns>
    public static IReadOnlyList<TranscriptSpan> FormatWithType(object? value)
    {
        var spans = new List<TranscriptSpan>();
        Append(spans, value, 0);
        var bounded = Bounded(spans).ToList();
        if (value is not null)
        {
            bounded.Add(new TranscriptSpan(" : " + TypeNameFormatter.Pretty(value.GetType()), SpanStyle.Dim));
        }

        return bounded;
    }

    /// <summary>
    /// Formats a value without its type.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <returns>The spans.</returns>
    public static IReadOnlyList<TranscriptSpan> Format(object? value)
    {
        var spans = new List<TranscriptSpan>();
        Append(spans, value, 0);
        return Bounded(spans);
    }

    private static void Append(List<TranscriptSpan> spans, object? value, int depth)
    {
        switch (value)
        {
            case null:
                spans.Add(new TranscriptSpan("null", SpanStyle.Keyword));
                return;
            case string s:
                spans.Add(new TranscriptSpan(LiteralParser.Escape(s), SpanStyle.String));
                return;
            case char c:
                spans.Add(new TranscriptSpan("'" + (c == '\'' ? "\\'" : c.ToString()) + "'", SpanStyle.String));
                return;
            case bool b:
                spans.Add(new TranscriptSpan(b ? "true" : "false", SpanStyle.Keyword));
                return;
            case IFormattable f when IsNumeric(value):
                spans.Add(new TranscriptSpan(f.ToString(null, CultureInfo.InvariantCulture), SpanStyle.Number));
                return;
            case Type t:
                spans.Add(new TranscriptSpan("typeof(" + TypeNameFormatter.Pretty(t) + ")", SpanStyle.Type));
                return;
            case Enum e:
                spans.Add(new TranscriptSpan(e.ToString()));
                return;
            case var _ when SessionAssemblies.IsSessionInstance(value):
                AppendSession(spans, value, depth, []);
                return;
            case Array array when array.Rank == 1 && depth < 2:
                AppendSequence(spans, array, "[", "]", depth);
                return;
            case IEnumerable enumerable when depth < 2 && value is not string:
                AppendSequence(spans, enumerable, "{", "}", depth);
                return;
            default:
                break;
        }

        var text = value.ToString() ?? "";
        var type = value.GetType();
        if (text == type.FullName || text == type.ToString())
        {
            spans.Add(new TranscriptSpan("{" + TypeNameFormatter.Pretty(type) + "}", SpanStyle.Dim));
        }
        else
        {
            spans.Add(new TranscriptSpan(text));
        }
    }

    private static void AppendSequence(List<TranscriptSpan> spans, IEnumerable items, string open, string close, int depth)
    {
        spans.Add(new TranscriptSpan(open));
        var count = 0;
        foreach (var item in items)
        {
            if (count > 0)
            {
                spans.Add(new TranscriptSpan(", "));
            }

            if (count++ >= MaxItems)
            {
                spans.Add(new TranscriptSpan("…", SpanStyle.Dim));
                break;
            }

            Append(spans, item, depth + 1);
        }

        spans.Add(new TranscriptSpan(close));
    }

    private static bool IsNumeric(object value) =>
        value is sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal or nint or nuint;

    /// <summary>
    /// True when the type, or a session base of it, overrides <c>ToString</c>. The slot is read
    /// without invoking anything: a delegate bound to the instance names the implementation.
    /// </summary>
    /// <param name="value">The instance.</param>
    /// <returns>True when a user body would run for ToString.</returns>
    public static bool HasOwnToString(object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var type = value.GetType();
        if (OverridesToString.TryGetValue(type, out var known))
        {
            return known.Value;
        }

        bool overridden;
        try
        {
            var bound = Delegate.CreateDelegate(typeof(Func<string>), value, typeof(object).GetMethod("ToString")!);
            var declaring = bound.Method.DeclaringType;
            overridden = declaring != typeof(object) && declaring != typeof(ValueType) && declaring != typeof(Enum);
        }
        catch (ArgumentException)
        {
            overridden = false;
        }

        OverridesToString.AddOrUpdate(type, new StrongBox<bool>(overridden));
        return overridden;
    }

    private static void AppendSession(List<TranscriptSpan> spans, object value, int depth, List<object> path)
    {
        var type = value.GetType();
        var name = TypeNameFormatter.Pretty(type);
        if (path.Any(p => ReferenceEquals(p, value)))
        {
            spans.Add(new TranscriptSpan("↺ " + name, SpanStyle.Dim));
            return;
        }

        if (HasOwnToString(value))
        {
            try
            {
                spans.Add(new TranscriptSpan(value.ToString() ?? "null"));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                spans.Add(new TranscriptSpan("{ToString threw " + ex.GetType().Name + "}", SpanStyle.Dim));
            }

            return;
        }

        if (depth >= MaxDepth)
        {
            spans.Add(new TranscriptSpan(name + " {…}", SpanStyle.Dim));
            return;
        }

        path.Add(value);
        try
        {
            spans.Add(new TranscriptSpan(name, SpanStyle.Type));
            var readers = ReadersFor(type);
            if (readers.Length == 0)
            {
                spans.Add(new TranscriptSpan(" { }"));
                return;
            }

            spans.Add(new TranscriptSpan(" { "));
            for (var i = 0; i < readers.Length; i++)
            {
                if (i > 0)
                {
                    spans.Add(new TranscriptSpan(", "));
                }

                if (i >= MaxFields)
                {
                    spans.Add(new TranscriptSpan("…", SpanStyle.Dim));
                    break;
                }

                var reader = readers[i];
                spans.Add(new TranscriptSpan(reader.Label, SpanStyle.Label));
                spans.Add(new TranscriptSpan(" = "));
                object? fieldValue;
                try
                {
                    fieldValue = reader.Read(value);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    spans.Add(new TranscriptSpan("{threw " + ex.GetType().Name + "}", SpanStyle.Dim));
                    continue;
                }

                if (fieldValue is not null and not Enum && SessionAssemblies.IsSessionInstance(fieldValue))
                {
                    AppendSession(spans, fieldValue, depth + 1, path);
                }
                else
                {
                    Append(spans, fieldValue, depth + 1);
                }
            }

            spans.Add(new TranscriptSpan(" }"));
        }
        finally
        {
            path.RemoveAt(path.Count - 1);
        }
    }

    private sealed record FieldReader(string Label, Func<object, object?> Read);

    /// <summary>
    /// The instance fields of a type and its bases, base fields first, each read by a generated
    /// method that skips visibility and runs nothing of the type. A name that appears more than
    /// once is qualified by its declaring type.
    /// </summary>
    private static FieldReader[] ReadersFor(Type type)
    {
        if (Readers.TryGetValue(type, out var cached))
        {
            return cached;
        }

        var chain = new List<Type>();
        for (var current = type; current is not null && current != typeof(object) && current != typeof(ValueType); current = current.BaseType)
        {
            chain.Insert(0, current);
        }

        var fields = chain.SelectMany(t => t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)).ToList();
        var duplicated = fields.GroupBy(f => f.Name, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        var readers = fields.Select(f => new FieldReader(duplicated.Contains(f.Name) ? TypeNameFormatter.Pretty(f.DeclaringType!) + "." + f.Name : f.Name, MakeReader(type, f))).ToArray();
        Readers.AddOrUpdate(type, readers);
        return readers;
    }

    private static Func<object, object?> MakeReader(Type type, FieldInfo field)
    {
        // The reader is owned by the session type's module, so it may read every field of the
        // type; ldfld runs no constructor or initializer, unlike FieldInfo.GetValue on a struct.
        var method = new DynamicMethod("read_" + field.Name, typeof(object), [typeof(object)], type.Module, skipVisibility: true);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        if (type.IsValueType)
        {
            il.Emit(OpCodes.Unbox, type);
        }
        else
        {
            il.Emit(OpCodes.Castclass, type);
        }

        il.Emit(OpCodes.Ldfld, field);
        if (field.FieldType.IsValueType || field.FieldType.IsGenericParameter)
        {
            il.Emit(OpCodes.Box, field.FieldType);
        }

        il.Emit(OpCodes.Ret);
        return method.CreateDelegate<Func<object, object?>>();
    }

    /// <summary>
    /// Cuts the text of a value to the display limit.
    /// </summary>
    /// <param name="spans">The spans of one value.</param>
    /// <returns>The spans, cut at the limit with an ellipsis.</returns>
    public static IReadOnlyList<TranscriptSpan> Bounded(IReadOnlyList<TranscriptSpan> spans)
    {
        ArgumentNullException.ThrowIfNull(spans);
        var total = 0;
        var kept = new List<TranscriptSpan>();
        foreach (var span in spans)
        {
            if (total + span.Text.Length > MaxLength)
            {
                var room = Math.Max(0, MaxLength - total);
                if (room > 0)
                {
                    kept.Add(span with { Text = span.Text[..room] });
                }

                kept.Add(new TranscriptSpan("…", SpanStyle.Dim));
                return kept;
            }

            kept.Add(span);
            total += span.Text.Length;
        }

        return kept;
    }
}
