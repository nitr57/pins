#region "copyright"

/*
    Copyright © 2026 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenCvSharp;
using System.IO;
using System.Linq;

namespace System.Windows.Media.Imaging {

    /// <summary>
    /// OpenCV decoding and encoding with WPF's failure behaviour: data that cannot be decoded throws
    /// <see cref="FileFormatException"/> (a missing file <see cref="FileNotFoundException"/>, a URI that is no file
    /// <see cref="NotSupportedException"/>), and an image that cannot be encoded throws
    /// <see cref="InvalidOperationException"/>, instead of an empty image or an empty stream. OpenCV cannot tell an
    /// unknown format from corrupt data, so both are a FileFormatException. A JPEG cut off after its headers still
    /// decodes, its missing part grey, since libjpeg only warns about a premature end.
    /// </summary>
    internal static class ImageCodec {

        public static Mat ReadFile(Uri uri) {
            if (uri.IsAbsoluteUri && !uri.IsFile) {
                throw new NotSupportedException($"Only images in files can be decoded here, not {uri}.");
            }

            string path = uri.IsAbsoluteUri ? uri.LocalPath : uri.OriginalString;

            if (!File.Exists(path)) {
                throw new FileNotFoundException("The image file does not exist.", path);
            }

            Mat mat;

            try {
                mat = Cv2.ImRead(path, ImreadModes.Unchanged);
            } catch (Exception ex) when (ex is OpenCVException or ArgumentException) {
                throw new FileFormatException(uri, $"The image file {path} could not be decoded.", ex);
            }

            if (mat is null || mat.Empty()) {
                mat?.Dispose();
                throw new FileFormatException(uri, $"The image file {path} could not be decoded.");
            }

            return mat;
        }

        public static Mat Decode(byte[] data) {
            if (data.Length == 0) {
                throw new FileFormatException("The image stream is empty.");
            }

            Mat mat;

            try {
                mat = Cv2.ImDecode(data, ImreadModes.Unchanged);
            } catch (Exception ex) when (ex is OpenCVException or ArgumentException) {
                throw new FileFormatException("The image stream could not be decoded.", ex);
            }

            if (mat is null || mat.Empty()) {
                mat?.Dispose();
                throw new FileFormatException("The image stream could not be decoded.");
            }

            return mat;
        }

        /// <summary>The rest of <paramref name="stream"/> from its position, also for a stream that cannot seek.</summary>
        public static byte[] ReadToEnd(Stream stream) {
            if (stream is MemoryStream memory && memory.Position == 0) {
                return memory.ToArray();
            }

            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);

            return buffer.ToArray();
        }

        public static byte[] Encode(string extension, Mat mat, params ImageEncodingParam[] parameters) {
            if (mat is null || mat.Empty()) {
                throw new InvalidOperationException($"Cannot encode an empty image as {extension}.");
            }

            // The int[] overload reports failure; the ImageEncodingParam one returns nothing.
            int[] flags = parameters.SelectMany(parameter => new[] { (int)parameter.EncodingId, parameter.Value }).ToArray();

            try {
                if (Cv2.ImEncode(extension, mat, out byte[] buffer, flags) && buffer.Length > 0) {
                    return buffer;
                }
            } catch (OpenCVException ex) {
                throw new InvalidOperationException($"Cannot encode a {Describe(mat)} image as {extension}: {ex.Message}", ex);
            }

            throw new InvalidOperationException($"Cannot encode a {Describe(mat)} image as {extension}.");
        }

        private static string Describe(Mat mat) {
            return $"{mat.Width}×{mat.Height} {mat.Type()}";
        }
    }
}
