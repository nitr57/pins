// SPDX-License-Identifier: MPL-2.0

using System.Text.Json;
using System.Text.Json.Serialization;

namespace NINA.GuideEngine.Algorithms;

/// <summary>Key of a stored periodic error: profile + mount (the worm belongs to the mount, not to the guide camera).</summary>
public sealed record PeriodicErrorKey(string ProfileId, string MountName)
{
    public string StorageKey => string.Join('|', ProfileId, MountName);
}

/// <summary>
/// In-memory keyed store of learned periodic errors (<see cref="PeriodicErrorModel"/>) with JSON persistence through
/// strings only; the host decides where the JSON lives. Not thread-safe.
/// </summary>
public sealed class PeriodicErrorStore
{
    /// <summary>Current JSON format version.</summary>
    public const int FormatVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);

    public int Count => entries.Count;

    public void Set(PeriodicErrorKey key, PeriodicErrorModel model)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(model);
        entries[key.StorageKey] = new Entry(key, model);
    }

    public PeriodicErrorModel? Get(PeriodicErrorKey key) => entries.TryGetValue(key.StorageKey, out var e) ? e.Model : null;

    public bool Remove(PeriodicErrorKey key) => entries.Remove(key.StorageKey);

    public string ToJson() => JsonSerializer.Serialize(new Document { Version = FormatVersion, Entries = [.. entries.Values] }, JsonOptions);

    /// <summary>Parses a store. Throws <see cref="JsonException"/> on malformed input.</summary>
    public static PeriodicErrorStore FromJson(string json)
    {
        var doc = JsonSerializer.Deserialize<Document>(json, JsonOptions);
        var store = new PeriodicErrorStore();
        if (doc?.Entries is null)
        {
            return store;
        }

        if (doc.Version > FormatVersion)
        {
            throw new JsonException($"Unsupported periodic error store version {doc.Version}");
        }

        foreach (var e in doc.Entries)
        {
            if (e?.Key is not null && e.Model is { PeriodSeconds: > 0, Sin.Count: > 0 } && e.Model.Sin.Count == e.Model.Cos.Count)
            {
                store.Set(e.Key, e.Model);
            }
        }

        return store;
    }

    private sealed record Entry(PeriodicErrorKey Key, PeriodicErrorModel Model);

    private sealed class Document
    {
        public int Version { get; set; }

        public List<Entry>? Entries { get; set; }
    }
}
