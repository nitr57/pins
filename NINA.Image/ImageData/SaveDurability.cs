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
using System.IO;

namespace NINA.Image.ImageData {

    /// <summary>
    /// Whether a saved image is forced onto the disk before the save returns. pins does that for every frame, so a
    /// save truly ends on a slow or network disk. The plate solver's temporary images are deleted again within
    /// seconds of the solve: left to the kernel's write-back (about 30 s), a file deleted before it never reaches the
    /// SD card of a Raspberry Pi, where forcing about 50 MB per solve cost 1–3 s and card wear.
    /// </summary>
    public static class SaveDurability {

        // BaseSolver's working directory (NINA.Platesolving)
        private static string PlateSolverWorkingDirectory => Path.Combine(CoreUtil.APPLICATIONTEMPPATH, "PlateSolver");

        public static bool NeedsFlushToDisk(string path) {
            return NeedsFlushToDisk(path, PlateSolverWorkingDirectory);
        }

        /// <summary>False for a file directly in <paramref name="temporaryDirectory"/>; true elsewhere, also in its subfolders.</summary>
        public static bool NeedsFlushToDisk(string path, string temporaryDirectory) {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(temporaryDirectory)) {
                return true;
            }

            try {
                var directory = Path.GetDirectoryName(Path.GetFullPath(path));
                var temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(temporaryDirectory));

                return !string.Equals(directory, temporary, StringComparison.Ordinal);
            } catch (Exception) {
                // an unusual path: keep the durable save
                return true;
            }
        }
    }
}
