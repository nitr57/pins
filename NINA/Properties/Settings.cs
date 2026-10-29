#region "copyright"

/*
    Copyright © 2016 - 2024 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Core.Utility;
using System;
using System.Configuration;
using System.IO;
using System.Runtime.Serialization;
using System.Xml;

namespace NINA.Properties
{

    /// <summary>
    /// Global application settings that are not tied to a specific profile
    /// </summary>
    [Serializable]
    [DataContract]
    public class Settings
    {

        private static Settings _default;

        /// <summary>
        /// Singleton instance for WPF-style Settings.Default access
        /// </summary>
        public static Settings Default
        {
            get
            {
                if (_default == null)
                {
                    _default = Load();
                }
                return _default;
            }
        }

        /// <summary>
        /// Whether the sidebar is collapsed
        /// </summary>
        [DataMember]
        public bool CollapsedSidebar { get; set; } = false;

        /// <summary>
        /// Database location path
        /// </summary>
        [DataMember]
        public string DatabaseLocation { get; set; } = Path.Combine(CoreUtil.APPLICATIONTEMPPATH, "NINA.sqlite");

        /// <summary>
        /// Whether to update settings from older versions
        /// </summary>
        [DataMember]
        public bool UpdateSettings { get; set; } = true;

        /// <summary>
        /// Whether to use saved profile selection
        /// </summary>
        [DataMember]
        public bool UseSavedProfileSelection { get; set; } = false;

        /// <summary>
        /// Auto-update source (Release, Beta, or Nightly)
        /// </summary>
        [DataMember]
        public int AutoUpdateSource { get; set; } = 0; // Default to RELEASE

        /// <summary>
        /// Application font family name
        /// </summary>
        [DataMember]
        public string ApplicationFontFamily { get; set; } = "Segoe UI";

        /// <summary>
        /// Whether SGP server is enabled
        /// </summary>
        [DataMember]
        public bool SGPServerEnabled { get; set; } = false;

        /// <summary>
        /// Font stretch setting
        /// </summary>
        [DataMember]
        public string FontStretch { get; set; } = "Normal";

        /// <summary>
        /// Font style setting
        /// </summary>
        [DataMember]
        public string FontStyle { get; set; } = "Normal";

        /// <summary>
        /// Font weight setting
        /// </summary>
        [DataMember]
        public string FontWeight { get; set; } = "Normal";

        /// <summary>
        /// Maximum size of the image save queue
        /// </summary>
        [DataMember]
        public int SaveQueueSize { get; set; } = 2;

        /// <summary>
        /// Whether to use single dock layout
        /// </summary>
        [DataMember]
        public bool SingleDockLayout { get; set; } = false;

        /// <summary>
        /// Whether hardware acceleration is enabled
        /// </summary>
        [DataMember]
        public bool HardwareAcceleration { get; set; } = true;

        /// <summary>
        /// Plugin repositories (JSON array)
        /// </summary>
        [DataMember]
        public string PluginRepositories { get; set; } = "[\"https://nighttime-imaging.eu/wp-json/nina/v1\"]";

        /// <summary>
        /// Window placement XML data
        /// </summary>
        [DataMember]
        public string WindowPlacement { get; set; } = "";

        private static readonly string SettingsFilePath = Path.Combine(CoreUtil.APPLICATIONTEMPPATH, "Settings.xml");

        // One save at a time: they share the temporary file
        private static readonly object SaveLock = new();

        /// <summary>
        /// Load the global app settings from disk
        /// </summary>
        public static Settings Load() => LoadFrom(SettingsFilePath);

        /// <summary>
        /// Loads the settings from <paramref name="path"/>, or from the copy the last save kept next to it
        /// (<c>.bkp</c>) when that file can't be read, e.g. after a power cut during an older, non-atomic save.
        /// </summary>
        public static Settings LoadFrom(string path)
        {
            if (!File.Exists(path) && !File.Exists(BackupPath(path)))
            {
                // Return default settings if file doesn't exist
                return new Settings();
            }

            if (TryRead(path, out var settings, out var error))
            {
                return settings;
            }

            if (TryRead(BackupPath(path), out settings, out var backupError))
            {
                Logger.Warning($"App settings {path} could not be read ({error?.Message ?? "missing"}), using the backup of the last save");
                return settings;
            }

            Logger.Error($"Failed to load app settings from {path} and its backup, using the defaults: {error?.Message ?? "missing"}; {backupError?.Message ?? "no backup"}");
            return new Settings();
        }

        private static string BackupPath(string path) => path + ".bkp";

        private static bool TryRead(string path, out Settings settings, out Exception error)
        {
            settings = null;
            error = null;
            if (!File.Exists(path))
            {
                return false;
            }

            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var serializer = new DataContractSerializer(typeof(Settings));
                    settings = (Settings)serializer.ReadObject(fs);
                    return settings != null;
                }
            }
            catch (Exception ex)
            {
                error = ex;
                return false;
            }
        }

        /// <summary>
        /// Save the global app settings to disk
        /// </summary>
        public void Save() => SaveTo(SettingsFilePath);

        /// <summary>
        /// Writes the settings to a temporary file, flushes it to the disk and only then puts it in place of
        /// <paramref name="path"/>, keeping the previous file as <c>.bkp</c>: a power cut or a full disk during the save
        /// leaves the old file intact instead of a truncated one, which the Reload after a failed save would read back.
        /// </summary>
        public void SaveTo(string path)
        {
            lock (SaveLock)
            {
                var tmp = path + ".tmp";
                try
                {
                    var directory = Path.GetDirectoryName(path);
                    if (!Directory.Exists(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        var serializer = new DataContractSerializer(typeof(Settings));
                        using (var writer = XmlWriter.Create(fs, new XmlWriterSettings { Indent = true }))
                        {
                            serializer.WriteObject(writer, this);
                        }

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
                }
                catch (Exception ex)
                {
                    Logger.Error($"Failed to save app settings to {path}", ex);
                    try
                    {
                        File.Delete(tmp);
                    }
                    catch
                    {
                        // nothing more to do; the next save overwrites it
                    }

                    throw;
                }
            }
        }

        /// <summary>
        /// Reload the global app settings from disk
        /// </summary>
        public void Reload()
        {
            var reloaded = Load();
            // Copy properties from reloaded instance to this instance
            this.CollapsedSidebar = reloaded.CollapsedSidebar;
            this.DatabaseLocation = reloaded.DatabaseLocation;
            this.UpdateSettings = reloaded.UpdateSettings;
            this.UseSavedProfileSelection = reloaded.UseSavedProfileSelection;
            this.AutoUpdateSource = reloaded.AutoUpdateSource;
            this.ApplicationFontFamily = reloaded.ApplicationFontFamily;
            this.SGPServerEnabled = reloaded.SGPServerEnabled;
            this.FontStretch = reloaded.FontStretch;
            this.FontStyle = reloaded.FontStyle;
            this.FontWeight = reloaded.FontWeight;
            this.SaveQueueSize = reloaded.SaveQueueSize;
            this.SingleDockLayout = reloaded.SingleDockLayout;
            this.HardwareAcceleration = reloaded.HardwareAcceleration;
            this.PluginRepositories = reloaded.PluginRepositories;
            this.WindowPlacement = reloaded.WindowPlacement;
        }
    }
}
