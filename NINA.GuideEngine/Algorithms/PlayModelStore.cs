// SPDX-License-Identifier: MPL-2.0

using System.Text.Json;
using System.Text.Json.Serialization;

namespace NINA.GuideEngine.Algorithms;

/// <summary>
/// In-memory keyed store of learned backlash play models (<see cref="PlayModelState"/>) with JSON persistence through
/// strings only; the host decides where the JSON lives. Keyed by profile + mount like the pulse model: the Dec gear belongs
/// to the mount. Not thread-safe.
/// </summary>
public sealed class PlayModelStore
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

    public void Set(PulseModelKey key, PlayModelState state)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(state);
        entries[key.StorageKey] = new Entry(key, state);
    }

    public PlayModelState? Get(PulseModelKey key) => entries.TryGetValue(key.StorageKey, out var e) ? e.State : null;

    public bool Remove(PulseModelKey key) => entries.Remove(key.StorageKey);

    public string ToJson() => JsonSerializer.Serialize(new Document { Version = FormatVersion, Entries = [.. entries.Values] }, JsonOptions);

    /// <summary>Parses a store. Throws <see cref="JsonException"/> on malformed input.</summary>
    public static PlayModelStore FromJson(string json)
    {
        var doc = JsonSerializer.Deserialize<Document>(json, JsonOptions);
        var store = new PlayModelStore();
        if (doc?.Entries is null)
        {
            return store;
        }

        if (doc.Version > FormatVersion)
        {
            throw new JsonException($"Unsupported backlash play model store version {doc.Version}");
        }

        foreach (var e in doc.Entries)
        {
            if (e?.Key is not null && e.State is not null)
            {
                store.Set(e.Key, e.State);
            }
        }

        return store;
    }

    private sealed record Entry(PulseModelKey Key, PlayModelState State);

    private sealed class Document
    {
        public int Version { get; set; }

        public List<Entry>? Entries { get; set; }
    }
}
