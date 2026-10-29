// SPDX-License-Identifier: MPL-2.0

#nullable enable

using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;
using NINA.Core.Utility;

namespace NINA.Equipment.Equipment.MyGuider.Internal;

/// <summary>
/// Reads and writes the internal guider's JSON stores (calibrations, periodic error, pulse model, backlash, play model),
/// each of which holds the entries of every profile and mount. A save goes to a temporary file that is flushed to the disk
/// before it replaces the store, keeping the previous file as <c>.bkp</c>; a store that can't be read is renamed aside
/// rather than overwritten by the next save, and the backup is read instead. Read-modify-write steps on one file run one
/// at a time through <see cref="Locked"/>: the guide loop's events and the API's clear and forget calls come from
/// different threads.
/// </summary>
internal static class StoreFile
{
    private static readonly ConcurrentDictionary<string, object> Locks = new(StringComparer.Ordinal);

    public static void Locked(string path, Action action)
    {
        lock (Locks.GetOrAdd(Path.GetFullPath(path), _ => new object()))
        {
            action();
        }
    }

    public static string BackupPath(string path) => path + ".bkp";

    /// <summary>The store at <paramref name="path"/>, else the backup of its last save, else <paramref name="empty"/>.</summary>
    public static T Read<T>(string path, Func<string, T> parse, Func<T> empty, string what)
    {
        T result = empty();
        Locked(path, () =>
        {
            if (File.Exists(path))
            {
                try
                {
                    result = parse(File.ReadAllText(path));
                    return;
                }
                catch (Exception ex)
                {
                    var aside = $"{path}.unreadable-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}";
                    Logger.Warning($"InternalGuider: could not read the {what} ({ex.Message}); kept as {Path.GetFileName(aside)}");
                    try
                    {
                        File.Move(path, aside, overwrite: true);
                    }
                    catch (Exception mex)
                    {
                        Logger.Warning($"InternalGuider: could not move the unreadable {what} aside: {mex.Message}");
                    }
                }
            }

            var backup = BackupPath(path);
            if (!File.Exists(backup))
            {
                return;
            }

            try
            {
                result = parse(File.ReadAllText(backup));
                Logger.Warning($"InternalGuider: using the {what} of the save before");
            }
            catch (Exception ex)
            {
                Logger.Warning($"InternalGuider: could not read the backup of the {what} either: {ex.Message}");
            }
        });

        return result;
    }

    public static void Write(string path, string content)
    {
        Locked(path, () =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var bytes = Encoding.UTF8.GetBytes(content);
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
            {
                File.Replace(tmp, path, BackupPath(path));
            }
            else
            {
                File.Move(tmp, path);
            }
        });
    }
}
