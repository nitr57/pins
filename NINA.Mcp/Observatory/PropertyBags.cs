// SPDX-License-Identifier: MPL-2.0

using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace NINA.Mcp.Observatory;

/// <summary>
/// Turns plain data objects (DTOs such as the internal guider's) into the JSON friendly property bags that
/// <see cref="IPinsObservatory"/> returns: nested <c>Dictionary&lt;string, object?&gt;</c> and <c>List&lt;object?&gt;</c> holding
/// only strings, booleans, numbers and null. Lets the adapter hand rich data to the tools without <c>NINA.Mcp</c>
/// referencing the DTO types.
/// </summary>
/// <remarks>
/// Rules: public readable instance properties become camelCase keys; dictionary keys are kept as they are. NaN and
/// infinities become null, enums and <see cref="Guid"/>s strings, <see cref="DateTime"/>s ISO 8601 strings (an
/// unspecified kind is taken as UTC, as the guider DTOs document), <see cref="TimeSpan"/>s seconds. Properties named in
/// <c>exclude</c> (case-insensitive, at every level) are left out, lists are cut to <c>maxItems</c> entries and objects
/// deeper than <c>maxDepth</c> become null.
/// </remarks>
public static class PropertyBags {

    public const int DefaultMaxDepth = 8;

    public const int DefaultMaxItems = 500;

    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> PropertyCache = new();

    /// <summary>
    /// The bag of <paramref name="value"/>'s properties. Null gives an empty bag; a value that is not an object with
    /// properties (a number, a list) is returned under the key "value".
    /// </summary>
    public static Dictionary<string, object?> FromObject(
        object? value,
        IReadOnlyCollection<string>? exclude = null,
        int maxDepth = DefaultMaxDepth,
        int maxItems = DefaultMaxItems) {
        var options = new Options(exclude, maxDepth, maxItems);
        var converted = Convert(value, options, 0);

        return converted switch {
            null => new Dictionary<string, object?>(),
            Dictionary<string, object?> bag => bag,
            _ => new Dictionary<string, object?> { ["value"] = converted }
        };
    }

    /// <summary>Each element of <paramref name="values"/> as a bag (see <see cref="FromObject"/>).</summary>
    public static List<IReadOnlyDictionary<string, object?>> FromList<T>(
        IEnumerable<T>? values,
        IReadOnlyCollection<string>? exclude = null,
        int maxDepth = DefaultMaxDepth,
        int maxItems = DefaultMaxItems) {
        if (values is null) {
            return [];
        }

        return values
            .Select(value => (IReadOnlyDictionary<string, object?>)FromObject(value, exclude, maxDepth, maxItems))
            .ToList();
    }

    /// <summary>A JSON friendly copy of any value (see the class remarks).</summary>
    public static object? ToValue(
        object? value,
        IReadOnlyCollection<string>? exclude = null,
        int maxDepth = DefaultMaxDepth,
        int maxItems = DefaultMaxItems) {
        return Convert(value, new Options(exclude, maxDepth, maxItems), 0);
    }

    private static object? Convert(object? value, Options options, int depth) {
        switch (value) {
            case null:
                return null;

            case string or bool:
                return value;

            case char character:
                return character.ToString();

            case double number:
                return double.IsFinite(number) ? number : null;

            case float number:
                return float.IsFinite(number) ? (double)number : null;

            case decimal or byte or sbyte or short or ushort or int or uint or long or ulong:
                return value;

            case Enum:
                return value.ToString();

            case Guid guid:
                return guid.ToString();

            case DateTime time:
                return ToIso(time);

            case DateTimeOffset time:
                return time.ToString("o", CultureInfo.InvariantCulture);

            case TimeSpan span:
                return span.TotalSeconds;

            case Type:
            case Delegate:
                return null;
        }

        if (depth >= options.MaxDepth) {
            return null;
        }

        if (value is IDictionary dictionary) {
            return ConvertDictionary(dictionary, options, depth);
        }

        if (value is IEnumerable sequence) {
            return ConvertSequence(sequence, options, depth);
        }

        return ConvertObject(value, options, depth);
    }

    private static Dictionary<string, object?> ConvertDictionary(IDictionary dictionary, Options options, int depth) {
        var bag = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (DictionaryEntry entry in dictionary) {
            var key = System.Convert.ToString(entry.Key, CultureInfo.InvariantCulture);

            if (string.IsNullOrEmpty(key) || options.IsExcluded(key)) {
                continue;
            }

            bag[key] = Convert(entry.Value, options, depth + 1);
        }

        return bag;
    }

    private static List<object?> ConvertSequence(IEnumerable sequence, Options options, int depth) {
        var list = new List<object?>();

        foreach (var item in sequence) {
            if (list.Count >= options.MaxItems) {
                break;
            }

            list.Add(Convert(item, options, depth + 1));
        }

        return list;
    }

    private static Dictionary<string, object?> ConvertObject(object value, Options options, int depth) {
        var bag = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var property in PropertyCache.GetOrAdd(value.GetType(), ReadableProperties)) {
            if (options.IsExcluded(property.Name)) {
                continue;
            }

            object? propertyValue;

            try {
                propertyValue = property.GetValue(value);
            } catch (TargetInvocationException) {
                continue;
            }

            bag[JsonNamingPolicy.CamelCase.ConvertName(property.Name)] = Convert(propertyValue, options, depth + 1);
        }

        return bag;
    }

    private static PropertyInfo[] ReadableProperties(Type type) {
        return type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.CanRead
                && property.GetIndexParameters().Length == 0
                && property.GetMethod is { IsPublic: true })
            .ToArray();
    }

    private static string ToIso(DateTime time) {
        var utc = time.Kind switch {
            DateTimeKind.Local => time.ToUniversalTime(),
            DateTimeKind.Unspecified => DateTime.SpecifyKind(time, DateTimeKind.Utc),
            _ => time
        };

        return utc.ToString("o", CultureInfo.InvariantCulture);
    }

    private sealed class Options {

        private readonly HashSet<string> excluded;

        public Options(IReadOnlyCollection<string>? exclude, int maxDepth, int maxItems) {
            excluded = exclude is null
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(exclude, StringComparer.OrdinalIgnoreCase);
            MaxDepth = Math.Max(1, maxDepth);
            MaxItems = Math.Max(0, maxItems);
        }

        public int MaxDepth { get; }

        public int MaxItems { get; }

        public bool IsExcluded(string name) {
            return excluded.Contains(name);
        }
    }
}
