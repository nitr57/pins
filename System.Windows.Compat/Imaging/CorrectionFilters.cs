#region "copyright"

/*
    Copyright © 2025 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenCvSharp;
using System.Drawing;

namespace Accord.Imaging.Filters {
    /// <summary>
    /// Brightness correction filter - adjusts image brightness
    /// </summary>
    public class BrightnessCorrection {
        private int adjustValue;

        public BrightnessCorrection(int adjustValue) {
            this.adjustValue = adjustValue;
        }

        public void ApplyInPlace(Bitmap image) {
            Mat mat = image;
            if (mat == null || mat.Empty()) return;

            // Add brightness value to all pixels
            mat.ConvertTo(mat, mat.Type(), 1.0, adjustValue);
        }
    }

    /// <summary>
    /// Contrast correction filter - adjusts image contrast like Accord's (AForge's LevelsLinear): a factor f in
    /// [-127, 127] above 0 stretches [f, 255 - f] to [0, 255], one below 0 compresses [0, 255] to [-f, 255 + f], 0 leaves
    /// the image as it is. The alpha channel of a 4-channel image is not changed.
    /// </summary>
    public class ContrastCorrection {
        private int contrastFactor;

        public ContrastCorrection(int contrastFactor) {
            this.contrastFactor = System.Math.Max(-127, System.Math.Min(127, contrastFactor));
        }

        public void ApplyInPlace(Bitmap image) {
            Mat mat = image;
            if (mat == null || mat.Empty() || mat.Depth() != MatType.CV_8U) return;

            using var lut = new Mat(1, 256, MatType.CV_8UC1);
            var table = Table(contrastFactor);
            for (int i = 0; i < 256; i++) {
                lut.Set(0, i, table[i]);
            }

            if (mat.Channels() == 4) {
                var channels = mat.Split();
                try {
                    for (int c = 0; c < 3; c++) {
                        Cv2.LUT(channels[c], lut, channels[c]);
                    }

                    Cv2.Merge(channels, mat);
                } finally {
                    foreach (var c in channels) {
                        c.Dispose();
                    }
                }
            } else {
                Cv2.LUT(mat, lut, mat);
            }
        }

        // AForge LevelsLinear.CalculateMap for the ranges ContrastCorrection.Factor sets
        internal static byte[] Table(int factor) {
            int inMin = factor > 0 ? factor : 0;
            int inMax = factor > 0 ? 255 - factor : 255;
            int outMin = factor > 0 ? 0 : -factor;
            int outMax = factor > 0 ? 255 : 255 + factor;
            double k = inMax == inMin ? 0 : (double)(outMax - outMin) / (inMax - inMin);
            double b = outMin - k * inMin;

            var table = new byte[256];
            for (int i = 0; i < 256; i++) {
                table[i] = i >= inMax ? (byte)outMax : i <= inMin ? (byte)outMin : (byte)(k * i + b);
            }

            return table;
        }
    }
}
