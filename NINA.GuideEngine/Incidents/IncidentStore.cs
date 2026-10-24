// SPDX-License-Identifier: MPL-2.0

using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NINA.GuideEngine.Imaging;

namespace NINA.GuideEngine.Incidents;

public enum IncidentImageKind
{
    /// <summary>The whole processed frame, mean-binned by <see cref="Incident.ContextBinning"/>.</summary>
    Context,

    /// <summary>A full-resolution processed frame of a key moment.</summary>
    Key,

    /// <summary>A star crop (primary or secondary), full resolution.</summary>
    Crop,
}

/// <summary>One stored image of an incident frame. Positions are sensor (frame) pixels.</summary>
public sealed record IncidentImage
{
    public long Frame { get; init; }

    public IncidentImageKind Kind { get; init; }

    /// <summary>Crops: index of the star in the frame's star list; 0 otherwise.</summary>
    public int Star { get; init; }

    /// <summary>Top-left corner in sensor pixels (0 for context and key images).</summary>
    public int X0 { get; init; }

    public int Y0 { get; init; }

    /// <summary>Size in the image's own pixels; one pixel covers <see cref="Binning"/> × <see cref="Binning"/> sensor pixels.</summary>
    public int Width { get; init; }

    public int Height { get; init; }

    public int Binning { get; init; } = 1;

    /// <summary>Exposure start (or the frame time when unknown).</summary>
    public DateTimeOffset Time { get; init; }

    public double ExposureMs { get; init; }

    /// <summary>Row-major 16-bit pixels.</summary>
    public ushort[] Pixels { get; init; } = [];

    /// <summary>Extra FITS keywords (preprocessing), values as FITS values (strings quoted).</summary>
    public IReadOnlyDictionary<string, string>? Keywords { get; init; }
}

/// <summary>
/// Disk budgets of an <see cref="IncidentStore"/>, per class (real equipment / simulator). An incident takes about 40-45 MB
/// for a single occurrence and 70-100 MB when ongoing (docs/INCIDENTS.md), so the default budget keeps the last 10-25
/// real incidents; hosts on small SD cards lower <see cref="BudgetBytes"/> (the plugin's IncidentBudgetMb setting).
/// </summary>
public sealed record IncidentStoreOptions
{
    /// <summary>One megabyte (MiB), the unit of the budgets.</summary>
    public const long MB = 1024L * 1024L;

    /// <summary>Budget of the real incidents: 1000 MB, about 10-25 incidents.</summary>
    public long BudgetBytes { get; init; } = 1000 * MB;

    /// <summary>Most real incidents kept, whatever their size: keeps the list and the startup scan short.</summary>
    public int MaxIncidents { get; init; } = 50;

    /// <summary>Budget of the simulator incidents: 200 MB, kept apart so that tests never rotate out real incidents.</summary>
    public long SimulatorBudgetBytes { get; init; } = 200 * MB;

    /// <summary>Most simulator incidents kept.</summary>
    public int SimulatorMaxIncidents { get; init; } = 50;

    /// <summary>
    /// Images are left out when saving them would leave less free disk space than this: 2 GB keeps room for the night's
    /// images, which matter more than an incident's frames.
    /// </summary>
    public long MinFreeBytes { get; init; } = 2048 * MB;
}

/// <summary>Disk use of an <see cref="IncidentStore"/>.</summary>
public readonly record struct IncidentStoreUsage(long Bytes, int Count, long SimulatorBytes, int SimulatorCount);

/// <summary>
/// Incidents on disk, one folder per incident: <c>incident.json</c> (the <see cref="Incident"/> with all frames),
/// <c>summary.json</c> (the same without frames, for fast listing; it also holds the kept flag), the images as multi-HDU
/// 16-bit FITS (<c>context.fits</c>, <c>crops.fits</c>, <c>key.fits</c>) and <c>images.json</c> (HDU offsets for random
/// access). Budgets per class (real / simulator) rotate out the oldest incidents that are not kept; a disk-space guard
/// leaves the images out when the disk runs full. Thread-safe; folders that cannot be read are skipped.
/// </summary>
public sealed class IncidentStore
{
    internal const string IncidentFile = "incident.json";
    internal const string SummaryFile = "summary.json";
    internal const string IndexFile = "images.json";

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object gate = new();
    private readonly Func<string, long> freeBytes;
    private readonly HashSet<string> reserved = new(StringComparer.Ordinal);
    private Dictionary<string, Incident>? summaries;
    private IncidentStoreOptions options;
    private (string Id, ImageIndex Index)? indexCache;

    /// <param name="directory">Folder of the incident folders (created when needed).</param>
    /// <param name="options">Budgets.</param>
    /// <param name="freeBytes">Free bytes on the disk of a folder (tests); default: the drive's available space.</param>
    public IncidentStore(string directory, IncidentStoreOptions options, Func<string, long>? freeBytes = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        Directory = directory;
        this.options = options ?? new IncidentStoreOptions();
        this.freeBytes = freeBytes ?? DriveFreeBytes;
    }

    /// <summary>An incident was written (new, or rewritten after it was reopened); the argument is its summary (no frames).</summary>
    public event EventHandler<Incident>? Saved;

    /// <summary>An incident was deleted (by request or by the budget rotation).</summary>
    public event EventHandler<string>? Deleted;

    public string Directory { get; }

    public IncidentStoreOptions Options
    {
        get
        {
            lock (gate)
            {
                return options;
            }
        }

        set
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (gate)
            {
                options = value;
            }
        }
    }

    public IncidentStoreUsage Usage
    {
        get
        {
            lock (gate)
            {
                var all = Summaries().Values;
                return new IncidentStoreUsage(
                    all.Where(s => !s.Tags.Simulator).Sum(s => s.SizeBytes), all.Count(s => !s.Tags.Simulator),
                    all.Where(s => s.Tags.Simulator).Sum(s => s.SizeBytes), all.Count(s => s.Tags.Simulator));
            }
        }
    }

    /// <summary>Whether <paramref name="id"/> can name an incident (ASCII letters, digits and '-').</summary>
    public static bool IsValidId(string? id) =>
        !string.IsNullOrEmpty(id) && id.Length <= 100 && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    /// <summary>Summaries (no frames), newest first.</summary>
    public IReadOnlyList<Incident> List()
    {
        lock (gate)
        {
            return Summaries().Values.OrderByDescending(s => s.Start).ThenByDescending(s => s.Id, StringComparer.Ordinal).ToList();
        }
    }

    public bool Exists(string id)
    {
        if (!IsValidId(id))
        {
            return false;
        }

        lock (gate)
        {
            return Summaries().ContainsKey(id);
        }
    }

    /// <summary>The incident with all frames, null when unknown or unreadable.</summary>
    public Incident? Get(string id)
    {
        Incident? summary;
        lock (gate)
        {
            if (!IsValidId(id) || !Summaries().TryGetValue(id, out summary))
            {
                return null;
            }
        }

        try
        {
            var full = JsonSerializer.Deserialize<Incident>(File.ReadAllText(Path.Combine(Folder(id), IncidentFile)), Json);
            return full is null ? null : Normalize(full) with
            {
                Kept = summary.Kept,
                SizeBytes = summary.SizeBytes,
                FramesOmitted = summary.FramesOmitted,
                FrameCount = full.Frames.Count,
            };
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>The context or key image of a frame, null when it has none.</summary>
    public IncidentImage? ReadImage(string id, IncidentImageKind kind, long frame)
    {
        var index = ReadIndex(id);
        var entry = index?.Images.FirstOrDefault(e => e.Kind == kind && e.Frame == frame);
        return entry is null ? null : ReadPixels(id, entry);
    }

    /// <summary>The star crops of a frame, primary first; empty when none were kept.</summary>
    public IReadOnlyList<IncidentImage> ReadCrops(string id, long frame)
    {
        var index = ReadIndex(id);
        if (index is null)
        {
            return [];
        }

        var list = new List<IncidentImage>();
        foreach (var e in index.Images.Where(e => e.Kind == IncidentImageKind.Crop && e.Frame == frame).OrderBy(e => e.Star))
        {
            if (ReadPixels(id, e) is { } img)
            {
                list.Add(img);
            }
        }

        return list;
    }

    /// <summary>Keeps (the rotation skips it) or releases an incident. Returns the updated summary, null when unknown.</summary>
    public Incident? SetKept(string id, bool kept)
    {
        lock (gate)
        {
            if (!IsValidId(id) || !Summaries().TryGetValue(id, out var s))
            {
                return null;
            }

            var updated = s with { Kept = kept };
            WriteAtomic(Path.Combine(Folder(id), SummaryFile), JsonSerializer.Serialize(updated, Json));
            summaries![id] = updated;
            return updated;
        }
    }

    public bool Delete(string id)
    {
        lock (gate)
        {
            if (!IsValidId(id) || (!Summaries().ContainsKey(id) && !System.IO.Directory.Exists(Folder(id))))
            {
                return false;
            }

            DeleteCore(id);
        }

        RaiseDeleted([id]);
        return true;
    }

    /// <summary>Deletes all incidents that are not kept; returns how many.</summary>
    public int DeleteAllNotKept()
    {
        List<string> ids;
        lock (gate)
        {
            ids = Summaries().Values.Where(s => !s.Kept).Select(s => s.Id).ToList();
            foreach (var id in ids)
            {
                DeleteCore(id);
            }
        }

        RaiseDeleted(ids);
        return ids.Count;
    }

    /// <summary>
    /// A free id <c>yyyyMMdd-HHmmss-Kind</c> (UTC) for an incident about to be recorded, with a -2, -3 … suffix when
    /// taken. The id stays reserved for this store instance.
    /// </summary>
    public string ReserveId(DateTimeOffset time, IncidentKind kind)
    {
        string baseId = time.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + kind;
        lock (gate)
        {
            var known = Summaries();
            string id = baseId;
            for (int n = 2; known.ContainsKey(id) || reserved.Contains(id) || System.IO.Directory.Exists(Folder(id)); n++)
            {
                id = baseId + "-" + n.ToString(CultureInfo.InvariantCulture);
            }

            reserved.Add(id);
            return id;
        }
    }

    /// <summary>
    /// Saves an incident with its images. When the incident exists already (reopened), its record is rewritten and the
    /// images are appended (the kept flag stays). First the oldest incidents of the same class that are not kept are
    /// deleted until the new data fits the budget; when it still doesn't fit, or when the images would leave less than
    /// <see cref="IncidentStoreOptions.MinFreeBytes"/> free, the images are left out (<see cref="Incident.FramesOmitted"/>).
    /// The frames' image flags are set from what is stored. Returns the stored summary.
    /// </summary>
    public Incident Save(Incident incident, IReadOnlyList<IncidentImage> images)
    {
        ArgumentNullException.ThrowIfNull(incident);
        images ??= [];
        string id = incident.Id;
        if (!IsValidId(id))
        {
            throw new ArgumentException($"invalid incident id '{id}'", nameof(incident));
        }

        bool sim = incident.Tags.Simulator;
        long imageBytes = images.Sum(i => FitsWriter.HduSize(i.Width, i.Height, HduKeywords(i).Count));
        long jsonBytes = JsonSerializer.SerializeToUtf8Bytes(incident, Json).LongLength;
        List<string> rotated;
        bool exists;
        IncidentFramesOmitted omitted = IncidentFramesOmitted.None;
        lock (gate)
        {
            exists = Summaries().ContainsKey(id);
            rotated = Rotate(sim, id, imageBytes + jsonBytes, isNew: !exists);
            var opts = options;
            var same = summaries!.Values.Where(s => s.Tags.Simulator == sim).ToList();
            long classBytes = same.Sum(s => s.SizeBytes);
            int count = same.Count + (exists ? 0 : 1);
            if (images.Count > 0)
            {
                if (classBytes + imageBytes + jsonBytes > (sim ? opts.SimulatorBudgetBytes : opts.BudgetBytes) || count > (sim ? opts.SimulatorMaxIncidents : opts.MaxIncidents))
                {
                    omitted = IncidentFramesOmitted.Budget;
                }
                else
                {
                    System.IO.Directory.CreateDirectory(Directory);
                    if (freeBytes(Directory) - imageBytes < opts.MinFreeBytes)
                    {
                        omitted = IncidentFramesOmitted.DiskSpace;
                    }
                }
            }
        }

        RaiseDeleted(rotated);

        string folder = Folder(id);
        string target = exists ? folder : Path.Combine(Directory, id + ".partial-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(target);
        try
        {
            var index = (exists ? ReadIndexFile(folder) : null) ?? new ImageIndex();
            if (omitted == IncidentFramesOmitted.None && images.Count > 0)
            {
                AppendImages(target, index, images);
            }

            WriteAtomic(Path.Combine(target, IndexFile), JsonSerializer.Serialize(index, Json));

            // the frames' image flags follow what is stored (older images of a reopened incident included)
            var byFrame = index.Images.ToLookup(e => e.Frame);
            var frames = incident.Frames.Select(f => f with
            {
                HasContext = byFrame[f.Frame].Any(e => e.Kind == IncidentImageKind.Context),
                HasKey = byFrame[f.Frame].Any(e => e.Kind == IncidentImageKind.Key),
                Crops = byFrame[f.Frame].Count(e => e.Kind == IncidentImageKind.Crop),
            }).ToList();
            long fitsBytes = index.Files.Values.Sum();
            long indexBytes = new FileInfo(Path.Combine(target, IndexFile)).Length;
            Incident summary;
            lock (gate)
            {
                bool kept = Summaries().TryGetValue(id, out var previous) ? previous.Kept : incident.Kept;
                var previousOmitted = previous?.FramesOmitted ?? IncidentFramesOmitted.None;
                var full = incident with
                {
                    Kept = kept,
                    Frames = frames,
                    FrameCount = frames.Count,
                    FramesOmitted = omitted != IncidentFramesOmitted.None ? omitted : previousOmitted,
                    SizeBytes = 0,
                };
                long jsonLength = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(full, Json))
                    + Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(full with { Frames = [] }, Json));
                full = full with { SizeBytes = fitsBytes + indexBytes + jsonLength };
                summary = full with { Frames = [] };
                WriteAtomic(Path.Combine(target, IncidentFile), JsonSerializer.Serialize(full, Json));
                WriteAtomic(Path.Combine(target, SummaryFile), JsonSerializer.Serialize(summary, Json));
                if (!exists)
                {
                    if (System.IO.Directory.Exists(folder))
                    {
                        System.IO.Directory.Delete(folder, recursive: true);
                    }

                    System.IO.Directory.Move(target, folder);
                }

                summaries![id] = summary;
                reserved.Remove(id);
                indexCache = null;
            }

            Saved?.Invoke(this, summary);
            return summary;
        }
        catch
        {
            if (!exists)
            {
                TryDeleteFolder(target);
            }

            throw;
        }
    }

    /// <summary>
    /// Writes the incident as a zip: incident.json, the FITS files that exist, the <paramref name="extras"/> (e.g. settings
    /// and log excerpts) and README.txt. False when the incident is unknown.
    /// </summary>
    public bool WriteZip(string id, Stream output, IEnumerable<(string Name, string Content)> extras)
    {
        ArgumentNullException.ThrowIfNull(output);
        var incident = Get(id);
        var index = ReadIndex(id);
        if (incident is null)
        {
            return false;
        }

        index ??= new ImageIndex();
        var names = new List<string>();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            void AddText(string name, string content)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                using var s = entry.Open();
                s.Write(Encoding.UTF8.GetBytes(content));
                names.Add(name);
            }

            AddText(IncidentFile, JsonSerializer.Serialize(incident, new JsonSerializerOptions(Json) { WriteIndented = true }));
            foreach (var kind in new[] { IncidentImageKind.Context, IncidentImageKind.Crop, IncidentImageKind.Key })
            {
                string file = FileOf(kind);
                if (!index.Files.TryGetValue(file, out long length) || length <= 0)
                {
                    continue;
                }

                var entry = zip.CreateEntry(file, CompressionLevel.Fastest);
                using var dst = entry.Open();
                using var src = new FileStream(Path.Combine(Folder(id), file), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                CopyBytes(src, dst, length);
                names.Add(file);
            }

            foreach (var (name, content) in extras ?? [])
            {
                string safe = Path.GetFileName(name ?? string.Empty);
                if (safe.Length > 0 && !names.Contains(safe) && safe != "README.txt")
                {
                    AddText(safe, content ?? string.Empty);
                }
            }

            AddText("README.txt", Readme(incident, index, names));
        }

        return true;
    }

    #region internals

    private string Folder(string id) => Path.Combine(Directory, id);

    private static string FileOf(IncidentImageKind kind) => kind switch
    {
        IncidentImageKind.Context => "context.fits",
        IncidentImageKind.Key => "key.fits",
        _ => "crops.fits",
    };

    private Dictionary<string, Incident> Summaries()
    {
        if (summaries is not null)
        {
            return summaries;
        }

        var map = new Dictionary<string, Incident>(StringComparer.Ordinal);
        if (System.IO.Directory.Exists(Directory))
        {
            foreach (var dir in System.IO.Directory.EnumerateDirectories(Directory))
            {
                string id = Path.GetFileName(dir);
                if (!IsValidId(id))
                {
                    continue;
                }

                if (ReadSummary(dir) is { } s && s.Id == id)
                {
                    map[id] = s;
                }
            }
        }

        summaries = map;
        return map;
    }

    private static Incident? ReadSummary(string dir)
    {
        foreach (var file in new[] { SummaryFile, IncidentFile })
        {
            try
            {
                string path = Path.Combine(dir, file);
                if (!File.Exists(path))
                {
                    continue;
                }

                var s = JsonSerializer.Deserialize<Incident>(File.ReadAllText(path), Json);
                if (s is not null)
                {
                    return Normalize(s) with { Frames = [], FrameCount = s.Frames.Count > 0 ? s.Frames.Count : s.FrameCount };
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
            {
                // try the next file; unreadable folders are skipped
            }
        }

        return null;
    }

    private List<string> Rotate(bool sim, string id, long needBytes, bool isNew)
    {
        var deleted = new List<string>();
        var opts = options;
        long budget = sim ? opts.SimulatorBudgetBytes : opts.BudgetBytes;
        int max = sim ? opts.SimulatorMaxIncidents : opts.MaxIncidents;
        while (true)
        {
            var same = Summaries().Values.Where(s => s.Tags.Simulator == sim).ToList();
            long bytes = same.Sum(s => s.SizeBytes);
            int count = same.Count + (isNew ? 1 : 0);
            if (bytes + needBytes <= budget && count <= max)
            {
                break;
            }

            var victim = same.Where(s => !s.Kept && s.Id != id).OrderBy(s => s.Start).ThenBy(s => s.Id, StringComparer.Ordinal).FirstOrDefault();
            if (victim is null)
            {
                break;
            }

            DeleteCore(victim.Id);
            deleted.Add(victim.Id);
        }

        return deleted;
    }

    private void DeleteCore(string id)
    {
        summaries?.Remove(id);
        if (indexCache is { } c && c.Id == id)
        {
            indexCache = null;
        }

        TryDeleteFolder(Folder(id));
    }

    private static void TryDeleteFolder(string path)
    {
        try
        {
            if (System.IO.Directory.Exists(path))
            {
                System.IO.Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort: a folder that cannot be deleted is skipped from now on (no summary)
        }
    }

    private void RaiseDeleted(IEnumerable<string> ids)
    {
        foreach (var id in ids)
        {
            try
            {
                Deleted?.Invoke(this, id);
            }
            catch
            {
                // subscribers must not break the store
            }
        }
    }

    private static void AppendImages(string folder, ImageIndex index, IReadOnlyList<IncidentImage> images)
    {
        foreach (var group in images.GroupBy(i => FileOf(i.Kind)))
        {
            string file = group.Key;
            long committed = index.Files.GetValueOrDefault(file);
            using var fs = new FileStream(Path.Combine(folder, file), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);

            // anything after the committed length is a leftover of an interrupted write
            fs.SetLength(committed);
            fs.Seek(committed, SeekOrigin.Begin);
            using var buffered = new BufferedStream(fs, 1 << 16);
            long position = committed;
            foreach (var img in group)
            {
                var (header, total) = FitsWriter.WriteHdu(buffered, img.Width, img.Height, img.Pixels, HduKeywords(img), primary: position == 0);
                index.Images.Add(new IndexEntry
                {
                    Kind = img.Kind,
                    Frame = img.Frame,
                    Star = img.Star,
                    File = file,
                    Offset = position,
                    DataOffset = position + header,
                    X0 = img.X0,
                    Y0 = img.Y0,
                    Width = img.Width,
                    Height = img.Height,
                    Binning = img.Binning,
                    Time = img.Time,
                    ExposureMs = img.ExposureMs,
                });
                position += total;
            }

            buffered.Flush();
            fs.Flush(flushToDisk: true);
            index.Files[file] = fs.Length;
        }
    }

    private static List<KeyValuePair<string, string>> HduKeywords(IncidentImage img)
    {
        var kw = new List<KeyValuePair<string, string>>
        {
            new("FRAME", img.Frame.ToString(CultureInfo.InvariantCulture)),
            new("KIND", FitsWriter.Quote(img.Kind.ToString().ToLowerInvariant())),
            new("STAR", img.Star.ToString(CultureInfo.InvariantCulture)),
            new("X0", img.X0.ToString(CultureInfo.InvariantCulture)),
            new("Y0", img.Y0.ToString(CultureInfo.InvariantCulture)),
            new("BINNING", img.Binning.ToString(CultureInfo.InvariantCulture)),
            new("DATE-OBS", FitsWriter.Quote(img.Time.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture))),
            new("EXPTIME", FitsWriter.Number(img.ExposureMs / 1000.0)),
        };
        if (img.Keywords is { } extra)
        {
            kw.AddRange(extra);
        }

        return kw;
    }

    private ImageIndex? ReadIndex(string id)
    {
        if (!IsValidId(id))
        {
            return null;
        }

        lock (gate)
        {
            if (!Summaries().ContainsKey(id))
            {
                return null;
            }

            if (indexCache is { } c && c.Id == id)
            {
                return c.Index;
            }
        }

        var index = ReadIndexFile(Folder(id));
        if (index is not null)
        {
            lock (gate)
            {
                indexCache = (id, index);
            }
        }

        return index;
    }

    private static ImageIndex? ReadIndexFile(string folder)
    {
        try
        {
            string path = Path.Combine(folder, IndexFile);
            return File.Exists(path) ? JsonSerializer.Deserialize<ImageIndex>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private IncidentImage? ReadPixels(string id, IndexEntry e)
    {
        try
        {
            int n = checked(e.Width * e.Height);
            var bytes = new byte[n * 2];
            using (var fs = new FileStream(Path.Combine(Folder(id), e.File), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                fs.Seek(e.DataOffset, SeekOrigin.Begin);
                fs.ReadExactly(bytes);
            }

            var pixels = new ushort[n];
            for (int i = 0; i < n; i++)
            {
                pixels[i] = (ushort)(BinaryPrimitives.ReadInt16BigEndian(bytes.AsSpan(i * 2, 2)) + 32768);
            }

            return new IncidentImage
            {
                Frame = e.Frame,
                Kind = e.Kind,
                Star = e.Star,
                X0 = e.X0,
                Y0 = e.Y0,
                Width = e.Width,
                Height = e.Height,
                Binning = e.Binning,
                Time = e.Time,
                ExposureMs = e.ExposureMs,
                Pixels = pixels,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException or OverflowException)
        {
            return null;
        }
    }

    private static void CopyBytes(Stream src, Stream dst, long length)
    {
        var buf = new byte[1 << 16];
        while (length > 0)
        {
            int read = src.Read(buf, 0, (int)Math.Min(buf.Length, length));
            if (read <= 0)
            {
                break;
            }

            dst.Write(buf, 0, read);
            length -= read;
        }
    }

    private static void WriteAtomic(string path, string content)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }

    private static long DriveFreeBytes(string directory)
    {
        try
        {
            return new DriveInfo(Path.GetFullPath(directory)).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return long.MaxValue;
        }
    }

    /// <summary>JSON numbers, strings and booleans of the diagnosis parameters back to double, string and bool.</summary>
    private static Incident Normalize(Incident incident)
    {
        if (incident.Diagnosis is not { } d)
        {
            return incident;
        }

        return incident with
        {
            Diagnosis = d with
            {
                Parameters = NormalizeParameters(d.Parameters),
                Evidence = d.Evidence.Select(e => e with { Parameters = NormalizeParameters(e.Parameters) }).ToList(),
            },
        };
    }

    private static IReadOnlyDictionary<string, object?> NormalizeParameters(IReadOnlyDictionary<string, object?>? p) =>
        (p ?? new Dictionary<string, object?>()).ToDictionary(kv => kv.Key, kv => kv.Value is JsonElement e ? FromJson(e) : kv.Value);

    private static object? FromJson(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Number => e.GetDouble(),
        JsonValueKind.String => e.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    private static string Readme(Incident incident, ImageIndex index, IReadOnlyList<string> files)
    {
        var sb = new StringBuilder();
        var inv = CultureInfo.InvariantCulture;
        sb.AppendLine(inv, $"PINS native guider incident {incident.Id}");
        sb.AppendLine(inv, $"{incident.Kind}, {incident.Start.UtcDateTime:yyyy-MM-dd HH:mm:ss} - {incident.End.UtcDateTime:HH:mm:ss} UTC, {incident.FrameCount} frames, end: {incident.EndReason}");
        if (incident.Diagnosis is { } d)
        {
            sb.AppendLine(inv, $"Likely cause: {d.Cause} - {d.Message}");
        }

        sb.AppendLine();
        sb.AppendLine("Files:");
        var descriptions = new Dictionary<string, string>
        {
            [IncidentFile] = "the incident: triggers, timeline markers, diagnosis, tags and the telemetry of every frame (JSON, sensor pixel coordinates)",
            ["context.fits"] = $"the whole processed guide frame of each frame with images, mean-binned {incident.ContextBinning}x{incident.ContextBinning} (one HDU per frame)",
            ["crops.fits"] = "star crops at full resolution: the primary star, then up to 8 secondaries (one HDU per crop)",
            ["key.fits"] = "full-resolution processed frames of the key moments: the last good frame before a trigger, the trigger frame, the first frame after recovery",
            ["settings.json"] = "guider settings",
            ["guide-log.txt"] = "the guide log lines of the incident's time (±1 min)",
            ["pins-log.txt"] = "the PINS log lines of the incident's time (±1 min), site location and home directory masked",
        };
        foreach (var f in files)
        {
            sb.AppendLine(inv, $"  {f}: {descriptions.GetValueOrDefault(f, "extra file")}");
        }

        sb.AppendLine("  README.txt: this file");
        sb.AppendLine();
        sb.AppendLine("FITS: 16-bit (BZERO 32768), pixels as the guider used them (after dark/defect correction and noise reduction).");
        sb.AppendLine("Each HDU has FRAME (frame number, see incident.json), KIND (context, crop, key), STAR (crops: index in the frame's");
        sb.AppendLine("star list), X0/Y0 (top-left corner in sensor pixels), BINNING (sensor pixels per image pixel), DATE-OBS (exposure");
        sb.AppendLine("start, UTC), EXPTIME (s) and the preprocessing: DARKSUB, DARKEXP, DARKLIB, DEFECTS, NOISERED, SWBIN.");
        sb.AppendLine(inv, $"Images: {index.Images.Count(i => i.Kind == IncidentImageKind.Context)} context, {index.Images.Count(i => i.Kind == IncidentImageKind.Crop)} crops, {index.Images.Count(i => i.Kind == IncidentImageKind.Key)} key frames.");
        if (incident.FramesOmitted != IncidentFramesOmitted.None)
        {
            sb.AppendLine(inv, $"Images were left out: {incident.FramesOmitted} (the telemetry is complete).");
        }

        return sb.ToString();
    }

    private sealed class ImageIndex
    {
        /// <summary>Committed length of each FITS file (a reader never reads beyond it).</summary>
        public Dictionary<string, long> Files { get; set; } = new(StringComparer.Ordinal);

        public List<IndexEntry> Images { get; set; } = [];
    }

    private sealed class IndexEntry
    {
        public IncidentImageKind Kind { get; set; }

        public long Frame { get; set; }

        public int Star { get; set; }

        public string File { get; set; } = string.Empty;

        /// <summary>Start of the HDU in the file.</summary>
        public long Offset { get; set; }

        /// <summary>Start of the HDU's pixel data in the file.</summary>
        public long DataOffset { get; set; }

        public int X0 { get; set; }

        public int Y0 { get; set; }

        public int Width { get; set; }

        public int Height { get; set; }

        public int Binning { get; set; }

        public DateTimeOffset Time { get; set; }

        public double ExposureMs { get; set; }
    }

    #endregion
}
