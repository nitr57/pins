// SPDX-License-Identifier: MPL-2.0

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Globalization;
using NINA.Core.Utility;
using NINA.GuideEngine.Core;
using NINA.GuideEngine.Imaging;

namespace NINA.Equipment.Equipment.MyGuider.Internal;

/// <summary>
/// Loads and saves guide camera dark libraries: the native library (multi-HDU FITS written by
/// <see cref="Save"/>) and, as a fallback, PHD2's dark library (~/.phd2/darks_defects/PHD2_dark_lib_*.fit, one HDU
/// per exposure with EXPOSURE in seconds) when its frame size matches the guide camera. A native library records the
/// guider's gain and offset setting (-1: the guide camera's own) and is not used with other ones; PHD2's records neither.
/// </summary>
internal static class DarkLibraryFiles
{
    public static string NativeDirectory => Path.Combine(InternalGuider.StorageDirectory, "Darks");

    public static IEnumerable<string> Phd2Directories =>
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".phd2", "darks_defects"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "PHD2", "darks_defects"),
    ];

    public static string NativePath(string cameraName, int width, int height, int binning)
    {
        var safe = new string((cameraName ?? "camera").Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
        return Path.Combine(NativeDirectory, $"darks_{safe}_{width}x{height}_bin{binning}.fits");
    }

    /// <summary>
    /// Loads the best available library for the camera; null when none matches. <paramref name="mismatch"/> says why the
    /// camera's native library was not used when it was built with another gain or offset; PHD2's library is not tried
    /// then, since it would not match either.
    /// </summary>
    public static (DarkLibrary Library, string Source)? Load(string cameraName, int sensorWidth, int sensorHeight, int binning, int gain, int offset,
        out string? mismatch) =>
        Load(NativePath(cameraName, sensorWidth, sensorHeight, binning), Phd2Directories, sensorWidth, sensorHeight, binning, gain, offset, out mismatch);

    internal static (DarkLibrary Library, string Source)? Load(string native, IEnumerable<string> phd2Directories, int sensorWidth, int sensorHeight,
        int binning, int gain, int offset, out string? mismatch)
    {
        mismatch = null;
        int w = sensorWidth / Math.Max(1, binning);
        int h = sensorHeight / Math.Max(1, binning);
        if (File.Exists(native))
        {
            if (TryLoad(native, w, h, binning, isPhd2: false, (gain, offset), out mismatch) is { } lib)
            {
                return (lib, native);
            }

            if (mismatch is not null)
            {
                return null;
            }
        }

        foreach (var dir in phd2Directories.Where(Directory.Exists))
        {
            foreach (var file in Directory.GetFiles(dir, "PHD2_dark_lib_*.fit*").OrderByDescending(File.GetLastWriteTimeUtc))
            {
                if (TryLoad(file, w, h, binning, isPhd2: true, null, out _) is { } plib)
                {
                    return (plib, file);
                }
            }
        }

        return null;
    }

    private static DarkLibrary? TryLoad(string path, int width, int height, int binning, bool isPhd2, (int Gain, int Offset)? camera, out string? mismatch)
    {
        mismatch = null;
        try
        {
            var hdus = FitsReader.ReadAll(File.ReadAllBytes(path));
            if (camera is { } c && hdus.Count > 0 && Mismatch(hdus[0].Header, c.Gain, c.Offset) is { } why)
            {
                mismatch = $"the dark library {Path.GetFileName(path)} was built with {why}; build it again for these settings";
                Logger.Warning($"InternalGuider: {mismatch}");
                return null;
            }

            var lib = new DarkLibrary();
            foreach (var (frame, header) in hdus)
            {
                if (frame.Width != width || frame.Height != height)
                {
                    continue;
                }

                if (!header.TryGetValue("EXPOSURE", out var e) && !header.TryGetValue("EXPTIME", out e))
                {
                    continue;
                }

                if (!double.TryParse(e, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) || seconds <= 0)
                {
                    continue;
                }

                frame.ExposureMs = seconds * 1000.0;
                frame.Binning = header.TryGetValue("XBINNING", out var b) && int.TryParse(b, out var bi) && bi > 0 ? bi : binning;
                lib.Add(frame);
            }

            if (lib.Count == 0)
            {
                return null;
            }

            Logger.Info($"InternalGuider: loaded {lib.Count} darks from {(isPhd2 ? "PHD2 library " : string.Empty)}{path}");
            return lib;
        }
        catch (Exception ex)
        {
            Logger.Warning($"InternalGuider: could not read dark library {path}: {ex.Message}");
            return null;
        }
    }

    // libraries built before the offset was recorded only have GAIN
    private static string? Mismatch(IReadOnlyDictionary<string, string> header, int gain, int offset)
    {
        var parts = new List<string>();
        if (Recorded(header, "GAIN") is { } g && g != gain)
        {
            parts.Add($"gain {Describe(g)} (now {Describe(gain)})");
        }

        if (Recorded(header, "OFFSET") is { } o && o != offset)
        {
            parts.Add($"offset {Describe(o)} (now {Describe(offset)})");
        }

        return parts.Count == 0 ? null : string.Join(" and ", parts);
    }

    private static int? Recorded(IReadOnlyDictionary<string, string> header, string key) =>
        header.TryGetValue(key, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? Math.Max(-1, i) : null;

    private static string Describe(int value) => value < 0 ? "of the guide camera's settings" : value.ToString(CultureInfo.InvariantCulture);

    public static void Save(string path, IReadOnlyList<GuideFrame> masters, int? gain, int? offset)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        using (var fs = File.Create(tmp))
        {
            FitsWriter.Write(fs, masters.Select(m => (m, (IReadOnlyDictionary<string, string>)new Dictionary<string, string>
            {
                ["EXPOSURE"] = (m.ExposureMs / 1000.0).ToString("0.###", CultureInfo.InvariantCulture),
                ["XBINNING"] = m.Binning.ToString(CultureInfo.InvariantCulture),
                ["GAIN"] = (gain ?? -1).ToString(CultureInfo.InvariantCulture),
                ["OFFSET"] = (offset ?? -1).ToString(CultureInfo.InvariantCulture),
            })).ToList());
            // on the disk before it replaces the library, which a power cut would otherwise leave empty
            fs.Flush(flushToDisk: true);
        }

        File.Move(tmp, path, overwrite: true);
    }
}
