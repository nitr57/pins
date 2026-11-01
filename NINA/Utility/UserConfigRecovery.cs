#region "copyright"

/*
    Copyright © 2026 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Core.Utility;
using System;
using System.Configuration;
using System.Globalization;
using System.IO;

namespace NINA.Utility {

    /// <summary>
    /// Keeps the plugins' user.config loadable. Plugins with .NET settings (ApplicationSettingsBase: ninaAPI,
    /// Touch-N-Stars, HocusFocus, ...) share one user.config per NINA version and read it in their
    /// constructors; an empty or half written file after a power cut stops them from loading. Upstream
    /// recovers in App.xaml.cs, which pins doesn't use: a readable file is backed up at start, an
    /// unreadable one is moved aside and the backup put back. Runs before the plugins load, so no
    /// restart is needed.
    /// </summary>
    internal static class UserConfigRecovery {

        public static void Run() {
            string path;
            try {
                path = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.PerUserRoamingAndLocal).FilePath;
            } catch (ConfigurationErrorsException ex) {
                path = ex.Filename;
            } catch (Exception ex) {
                Logger.Warning($"Could not find the plugins' user.config: {ex.Message}");
                return;
            }

            if (string.IsNullOrEmpty(path)) {
                return;
            }

            try {
                Recover(path);
            } catch (Exception ex) {
                Logger.Warning($"Could not check the plugins' user.config {path}: {ex.Message}");
            }
        }

        public static string BackupPath(string path) => path + ".bkp";

        /// <summary>Backs up a readable file, or replaces an unreadable one with its backup.</summary>
        internal static void Recover(string path) {
            if (!File.Exists(path)) {
                return;
            }

            if (IsReadable(path, out var reason)) {
                // Through a temporary file, so a power cut while copying can't leave a half backup
                var backup = BackupPath(path);
                var tmp = backup + ".tmp";
                File.Copy(path, tmp, overwrite: true);
                File.Move(tmp, backup, overwrite: true);
                return;
            }

            var aside = $"{path}.unreadable-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}";
            File.Move(path, aside, overwrite: true);

            var bkp = BackupPath(path);
            if (File.Exists(bkp) && IsReadable(bkp, out _)) {
                File.Copy(bkp, path);
                Logger.Warning($"The plugins' user.config could not be read ({reason}); kept as {Path.GetFileName(aside)} and restored from the backup of the last start");
            } else {
                Logger.Warning($"The plugins' user.config could not be read ({reason}); kept as {Path.GetFileName(aside)}. No usable backup, the plugins start with their default settings");
            }
        }

        /// <summary>Whether the configuration system can open the file as a user.config.</summary>
        internal static bool IsReadable(string path, out string reason) {
            reason = null;
            try {
                var dir = Path.GetDirectoryName(Path.GetFullPath(path));

                // Exe and roaming files that don't exist read as empty; only the file itself is parsed
                var map = new ExeConfigurationFileMap {
                    ExeConfigFilename = Path.Combine(dir, "pins-none.exe.config"),
                    RoamingUserConfigFilename = Path.Combine(dir, "pins-none.roaming.config"),
                    LocalUserConfigFilename = path
                };
                ConfigurationManager.OpenMappedExeConfiguration(map, ConfigurationUserLevel.PerUserRoamingAndLocal);
                return true;
            } catch (ConfigurationErrorsException ex) {
                reason = ex.BareMessage;
                return false;
            }
        }
    }
}
