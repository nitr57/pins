#region "copyright"

/*
    Copyright © 2025 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System;
using System.IO;
using OpenCvSharp;

namespace System.Windows.Media.Imaging {
    /// <summary>
    /// TIFF compression options
    /// </summary>
    public enum TiffCompressOption {
        None,
        Lzw,
        Zip
    }

    /// <summary>
    /// Base class for bitmap encoders
    /// </summary>
    public abstract class BitmapEncoder {
        public BitmapFrameCollection Frames { get; } = new BitmapFrameCollection();

        public abstract void Save(Stream stream);
    }

    /// <summary>
    /// TIFF bitmap encoder
    /// </summary>
    public class TiffBitmapEncoder : BitmapEncoder {
        public TiffCompressOption Compression { get; set; } = TiffCompressOption.None;

        public override void Save(Stream stream) {
            if (Frames.Count == 0) {
                throw new InvalidOperationException("No frames to encode");
            }

            // Use the first frame's bitmap source
            var frame = Frames[0];
            using var frameLease = frame.LeaseMat();
            Mat mat = frameLease.Mat;

            byte[] buffer = ImageCodec.Encode(".tif", mat);
            stream.Write(buffer, 0, buffer.Length);
        }
    }

    /// <summary>
    /// PNG bitmap encoder
    /// </summary>
    public class PngBitmapEncoder : BitmapEncoder {
        public override void Save(Stream stream) {
            if (Frames.Count == 0) {
                throw new InvalidOperationException("No frames to encode");
            }

            var frame = Frames[0];
            using var frameLease = frame.LeaseMat();
            Mat mat = frameLease.Mat;

            byte[] buffer = ImageCodec.Encode(".png", mat, new ImageEncodingParam(ImwriteFlags.PngCompression, 3));
            stream.Write(buffer, 0, buffer.Length);
        }
    }

    /// <summary>
    /// JPEG bitmap encoder
    /// </summary>
    public class JpegBitmapEncoder : BitmapEncoder {
        public int QualityLevel { get; set; } = 90;

        public override void Save(Stream stream) {
            if (Frames.Count == 0) {
                throw new InvalidOperationException("No frames to encode");
            }

            // Use the first frame's bitmap source
            var frame = Frames[0];
            using var frameLease = frame.LeaseMat();
            Mat mat = frameLease.Mat;

            if (mat == null || mat.Empty()) {
                throw new InvalidOperationException("Cannot encode an empty image as .jpg.");
            }

            // JPEG only supports 8-bit, so convert if necessary
            Mat matToEncode = mat;
            bool needsCleanup = false;

            if (mat.Depth() == MatType.CV_16U) {
                // Convert 16-bit to 8-bit for JPEG
                matToEncode = new Mat();
                mat.ConvertTo(matToEncode, MatType.CV_8U, 1.0 / 256.0);
                needsCleanup = true;
            }

            try {
                byte[] buffer = ImageCodec.Encode(".jpg", matToEncode, new ImageEncodingParam(ImwriteFlags.JpegQuality, QualityLevel));
                stream.Write(buffer, 0, buffer.Length);
            } finally {
                if (needsCleanup) {
                    matToEncode.Dispose();
                }
            }
        }
    }

    /// <summary>
    /// Collection of bitmap frames for encoding
    /// </summary>
    public class BitmapFrameCollection : System.Collections.Generic.List<BitmapFrame> {
    }
}
