// SPDX-License-Identifier: MPL-2.0

using System.Text.Json;
using System.Text.Json.Serialization;

namespace NINA.GuideEngine.Algorithms;

/// <summary>Key of a stored pulse model: profile + mount, like the periodic error (the gears belong to the mount).</summary>
public sealed record PulseModelKey(string ProfileId, string MountName)
{
    public string StorageKey => string.Join('|', ProfileId, MountName);
}

/// <summary>
/// In-memory keyed store of learned pulse models (<see cref="PulseModelState"/>) with JSON persistence through strings
/// only; the host decides where the JSON lives. A state also names the calibration it holds with. Not thread-safe.
/// </summary>
public sealed class PulseModelStore
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

    public void Set(PulseModelKey key, PulseModelState state)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(state);
        entries[key.StorageKey] = new Entry(key, state);
    }

    public PulseModelState? Get(PulseModelKey key) => entries.TryGetValue(key.StorageKey, out var e) ? e.State : null;

    public bool Remove(PulseModelKey key) => entries.Remove(key.StorageKey);

    public string ToJson() => JsonSerializer.Serialize(new Document { Version = FormatVersion, Entries = [.. entries.Values] }, JsonOptions);

    /// <summary>Parses a store. Throws <see cref="JsonException"/> on malformed input.</summary>
    public static PulseModelStore FromJson(string json)
    {
        var doc = JsonSerializer.Deserialize<Document>(json, JsonOptions);
        var store = new PulseModelStore();
        if (doc?.Entries is null)
        {
            return store;
        }

        if (doc.Version > FormatVersion)
        {
            throw new JsonException($"Unsupported pulse model store version {doc.Version}");
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

    private sealed record Entry(PulseModelKey Key, PulseModelState State);

    private sealed class Document
    {
        public int Version { get; set; }

        public List<Entry>? Entries { get; set; }
    }
}
