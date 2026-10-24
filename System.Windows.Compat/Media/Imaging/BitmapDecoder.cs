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
using System;
using System.Collections.Generic;
using System.IO;

namespace System.Windows.Media.Imaging {
    /// <summary>
    /// Color context for color management
    /// </summary>
    public class ColorContext {
    }

    /// <summary>
    /// BitmapCreateOptions - options for creating bitmaps
    /// </summary>
    [Flags]
    public enum BitmapCreateOptions {
        None = 0,
        PreservePixelFormat = 1,
        DelayCreation = 2,
        IgnoreColorProfile = 4,
        IgnoreImageCache = 8
    }

    /// <summary>
    /// TiffBitmapDecoder - decodes TIFF images using OpenCV
    /// </summary>
    public class TiffBitmapDecoder : BitmapDecoder {
        public TiffBitmapDecoder(Stream stream, BitmapCreateOptions createOptions, BitmapCacheOption cacheOption) {
            LoadFromStream(stream);
        }

        public TiffBitmapDecoder(Uri uri, BitmapCreateOptions createOptions, BitmapCacheOption cacheOption) {
            if (uri.IsFile) {
                LoadFromFile(uri.LocalPath);
            }
        }

        private void LoadFromStream(Stream stream) {
            // Read stream into memory
            byte[] buffer;
            if (stream is MemoryStream ms) {
                buffer = ms.ToArray();
            } else {
                using (var memStream = new MemoryStream()) {
                    stream.CopyTo(memStream);
                    buffer = memStream.ToArray();
                }
            }

            // Decode using OpenCV
            Mat mat = Cv2.ImDecode(buffer, ImreadModes.Unchanged);
            if (mat != null && !mat.Empty()) {
                var frame = new BitmapFrame(mat, foreignMat: false);
                Frames.Add(frame);
            }
        }

        private void LoadFromFile(string filePath) {
            Mat mat = Cv2.ImRead(filePath, ImreadModes.Unchanged);
            if (mat != null && !mat.Empty()) {
                var frame = new BitmapFrame(mat, foreignMat: false);
                Frames.Add(frame);
            }
        }
    }

    /// <summary>
    /// GifBitmapDecoder - decodes GIF images using OpenCV
    /// </summary>
    public class GifBitmapDecoder : BitmapDecoder {
        public GifBitmapDecoder(Uri uri, BitmapCreateOptions createOptions, BitmapCacheOption cacheOption) {
            if (uri.IsFile) {
                Mat mat = Cv2.ImRead(uri.LocalPath, ImreadModes.Unchanged);
                if (mat != null && !mat.Empty()) {
                    var frame = new BitmapFrame(mat, foreignMat: false);
                    Frames.Add(frame);
                }
            }
        }
    }

    /// <summary>
    /// JpegBitmapDecoder - decodes JPEG images using OpenCV
    /// </summary>
    public class JpegBitmapDecoder : BitmapDecoder {
        public JpegBitmapDecoder(Uri uri, BitmapCreateOptions createOptions, BitmapCacheOption cacheOption) {
            if (uri.IsFile) {
                Mat mat = Cv2.ImRead(uri.LocalPath, ImreadModes.Unchanged);
                if (mat != null && !mat.Empty()) {
                    var frame = new BitmapFrame(mat, foreignMat: false);
                    Frames.Add(frame);
                }
            }
        }

        public JpegBitmapDecoder(Stream stream, BitmapCreateOptions createOptions, BitmapCacheOption cacheOption) {
            byte[] data = new byte[stream.Length - stream.Position];
            // A single Read can return fewer bytes than requested (network/chunked streams) -
            // loop until the buffer is full, matching BitmapImage.SetSource's read loop.
            int offset = 0;
            int bytesRead;
            while (offset < data.Length && (bytesRead = stream.Read(data, offset, data.Length - offset)) > 0) {
                offset += bytesRead;
            }
            Mat mat = Cv2.ImDecode(data, ImreadModes.Unchanged);
            if (mat != null && !mat.Empty()) {
                var frame = new BitmapFrame(mat, foreignMat: false);
                Frames.Add(frame);
            }
        }
    }

    /// <summary>
    /// PngBitmapDecoder - decodes PNG images using OpenCV
    /// </summary>
    public class PngBitmapDecoder : BitmapDecoder {
        public PngBitmapDecoder(Uri uri, BitmapCreateOptions createOptions, BitmapCacheOption cacheOption) {
            if (uri.IsFile) {
                Mat mat = Cv2.ImRead(uri.LocalPath, ImreadModes.Unchanged);
                if (mat != null && !mat.Empty()) {
                    var frame = new BitmapFrame(mat, foreignMat: false);
                    Frames.Add(frame);
                }
            }
        }
    }

    /// <summary>
    /// BitmapFrame - represents a frame in a decoded bitmap
    /// </summary>
    public class BitmapFrame : BitmapSource {
        public BitmapFrame(Mat mat) : base(mat) { }

        /// <summary>
        /// For Mats this assembly allocated itself (decoded images, clones, shared headers over an
        /// OpenCV-owned buffer); see BitmapSource(Mat, bool).
        /// </summary>
        internal BitmapFrame(Mat mat, bool foreignMat) : base(mat, foreignMat) { }

        /// <summary>
        /// Metadata associated with the frame
        /// </summary>
        public BitmapMetadata Metadata { get; set; }

        public static BitmapFrame Create(BitmapSource source) {
            return new BitmapFrame(ShareOrCloneMat(source), foreignMat: false);
        }

        public static BitmapFrame Create(BitmapSource source, BitmapSource thumbnail, BitmapMetadata metadata, System.Collections.ObjectModel.ReadOnlyCollection<ColorContext> colorContexts) {
            var frame = new BitmapFrame(ShareOrCloneMat(source), foreignMat: false);
            frame.Metadata = metadata;
            return frame;
        }

        /// <summary>
        /// Shares a frozen source's pixel buffer when that is safe (see BitmapSource.TryShareMat),
        /// otherwise takes a copy through the implicit conversion as before. Either way the frame
        /// stays valid after the source is disposed. A null source still throws
        /// NullReferenceException, as the implicit conversion always did.
        /// </summary>
        private static Mat ShareOrCloneMat(BitmapSource source) {
            return source.TryShareMat() ?? (Mat)source ?? new Mat();
        }
    }

    /// <summary>
    /// Base class for bitmap decoders
    /// </summary>
    public abstract class BitmapDecoder {
        public List<BitmapFrame> Frames { get; protected set; } = new List<BitmapFrame>();

        public BitmapFrame Preview => Frames.Count > 0 ? Frames[0] : null;

        public BitmapFrame Thumbnail => Frames.Count > 0 ? Frames[0] : null;
    }
}
