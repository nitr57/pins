// SPDX-License-Identifier: MPL-2.0

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using NINA.GuideEngine.Core;

namespace NINA.GuideEngine.Calibration;

/// <summary>Key of a stored calibration: profile + guide camera + mount + binning + focal length (ARCHITECTURE.md, "Calibration").</summary>
public sealed record CalibrationKey(string ProfileId, string CameraName, string MountName, int Binning, double FocalLengthMm)
{
    /// <summary>Normalised string form used for lookups (focal length rounded to 0.1 mm, names case-sensitive).</summary>
    public string StorageKey =>
        string.Join('|', ProfileId, CameraName, MountName, Binning.ToString(CultureInfo.InvariantCulture),
            Math.Round(FocalLengthMm, 1).ToString("0.0", CultureInfo.InvariantCulture));
}

/// <summary>Validity of a stored calibration against the current optics.</summary>
public enum CalibrationValidity
{
    Valid,
    NotFound,

    /// <summary>The stored data itself is not a usable calibration.</summary>
    Invalid,
    PixelSizeChanged,
    FocalLengthChanged,
    BinningChanged,
}

/// <summary>Result of <see cref="CalibrationStore.Lookup"/>.</summary>
public sealed record CalibrationLookup(CalibrationValidity Validity, CalibrationData? Calibration)
{
    /// <summary>The calibration when it may be used, else null.</summary>
    public CalibrationData? Usable => Validity == CalibrationValidity.Valid ? Calibration : null;
}

/// <summary>
/// In-memory keyed store of calibrations with JSON (System.Text.Json) persistence through strings/streams only;
/// the host decides where the JSON lives. Not thread-safe.
/// </summary>
public sealed class CalibrationStore
{
    /// <summary>Current JSON format version.</summary>
    public const int FormatVersion = 1;

    /// <summary>Relative pixel size / focal length difference that invalidates a calibration.</summary>
    public const double RelativeTolerance = 0.01;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);

    public int Count => entries.Count;

    public IEnumerable<KeyValuePair<CalibrationKey, CalibrationData>> Entries =>
        entries.Values.Select(e => new KeyValuePair<CalibrationKey, CalibrationData>(e.Key, e.Calibration));

    public void Set(CalibrationKey key, CalibrationData calibration)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(calibration);
        entries[key.StorageKey] = new Entry(key, calibration);
    }

    public bool TryGet(CalibrationKey key, out CalibrationData? calibration)
    {
        ArgumentNullException.ThrowIfNull(key);
        bool found = entries.TryGetValue(key.StorageKey, out Entry? e);
        calibration = e?.Calibration;
        return found;
    }

    public bool Remove(CalibrationKey key) => entries.Remove(key.StorageKey);

    /// <summary>Removes all calibrations of a profile (e.g. PHD2 clear_calibration for the profile).</summary>
    public int RemoveProfile(string profileId)
    {
        var keys = entries.Where(kv => kv.Value.Key.ProfileId == profileId).Select(kv => kv.Key).ToList();
        foreach (string k in keys)
        {
            entries.Remove(k);
        }

        return keys.Count;
    }

    public void Clear() => entries.Clear();

    /// <summary>Looks up a calibration and checks it against the current optics.</summary>
    public CalibrationLookup Lookup(CalibrationKey key, GuideOptics currentOptics)
    {
        if (!TryGet(key, out CalibrationData? cal) || cal is null)
        {
            return new CalibrationLookup(CalibrationValidity.NotFound, null);
        }

        return new CalibrationLookup(CheckValidity(cal, currentOptics), cal);
    }

    /// <summary>
    /// Checks a calibration against the current optics: invalid when the pixel size, focal length (by more than
    /// <see cref="RelativeTolerance"/>) or binning changed. Unknown (0) values on either side are not compared.
    /// </summary>
    public static CalibrationValidity CheckValidity(CalibrationData cal, GuideOptics currentOptics)
    {
        if (!cal.IsValid)
        {
            return CalibrationValidity.Invalid;
        }

        if (Differs(cal.PixelSizeUm, currentOptics.PixelSizeUm))
        {
            return CalibrationValidity.PixelSizeChanged;
        }

        if (Differs(cal.FocalLengthMm, currentOptics.FocalLengthMm))
        {
            return CalibrationValidity.FocalLengthChanged;
        }

        if (cal.Binning != currentOptics.Binning)
        {
            return CalibrationValidity.BinningChanged;
        }

        return CalibrationValidity.Valid;
    }

    public string ToJson() => JsonSerializer.Serialize(ToDocument(), JsonOptions);

    public void Save(Stream stream) => JsonSerializer.Serialize(stream, ToDocument(), JsonOptions);

    /// <summary>Parses a store. Throws <see cref="JsonException"/> on malformed input.</summary>
    public static CalibrationStore FromJson(string json) => FromDocument(JsonSerializer.Deserialize<Document>(json, JsonOptions));

    public static CalibrationStore Load(Stream stream) => FromDocument(JsonSerializer.Deserialize<Document>(stream, JsonOptions));

    /// <summary>Serialises one calibration.</summary>
    public static string SerializeCalibration(CalibrationData cal) => JsonSerializer.Serialize(cal, JsonOptions);

    public static CalibrationData? DeserializeCalibration(string json) => JsonSerializer.Deserialize<CalibrationData>(json, JsonOptions);

    private static bool Differs(double stored, double current) =>
        stored > 0 && current > 0 && Math.Abs(stored - current) > RelativeTolerance * stored;

    private Document ToDocument() => new()
    {
        Version = FormatVersion,
        Entries = entries.Values.Select(e => new Entry(e.Key, e.Calibration)).ToList(),
    };

    private static CalibrationStore FromDocument(Document? doc)
    {
        var store = new CalibrationStore();
        if (doc?.Entries is null)
        {
            return store;
        }

        if (doc.Version > FormatVersion)
        {
            throw new JsonException($"Unsupported calibration store version {doc.Version}");
        }

        foreach (Entry e in doc.Entries)
        {
            if (e?.Key is not null && e.Calibration is not null)
            {
                store.Set(e.Key, e.Calibration);
            }
        }

        return store;
    }

    private sealed record Entry(CalibrationKey Key, CalibrationData Calibration);

    private sealed class Document
    {
        public int Version { get; set; }

        public List<Entry>? Entries { get; set; }
    }
}
