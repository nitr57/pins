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
/// per exposure with EXPOSURE in seconds) when its frame size matches the guide camera.
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

    /// <summary>Loads the best available library for the camera; null when none matches.</summary>
    public static (DarkLibrary Library, string Source)? Load(string cameraName, int sensorWidth, int sensorHeight, int binning)
    {
        int w = sensorWidth / Math.Max(1, binning);
        int h = sensorHeight / Math.Max(1, binning);
        var native = NativePath(cameraName, sensorWidth, sensorHeight, binning);
        if (File.Exists(native) && TryLoad(native, w, h, binning, isPhd2: false) is { } lib)
        {
            return (lib, native);
        }

        foreach (var dir in Phd2Directories.Where(Directory.Exists))
        {
            foreach (var file in Directory.GetFiles(dir, "PHD2_dark_lib_*.fit*").OrderByDescending(File.GetLastWriteTimeUtc))
            {
                if (TryLoad(file, w, h, binning, isPhd2: true) is { } plib)
                {
                    return (plib, file);
                }
            }
        }

        return null;
    }

    private static DarkLibrary? TryLoad(string path, int width, int height, int binning, bool isPhd2)
    {
        try
        {
            var hdus = FitsReader.ReadAll(File.ReadAllBytes(path));
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

    public static void Save(string path, IReadOnlyList<GuideFrame> masters, int? gain)
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
            })).ToList());
        }

        File.Move(tmp, path, overwrite: true);
    }
}
