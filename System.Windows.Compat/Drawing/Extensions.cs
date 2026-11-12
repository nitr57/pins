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
using System.Drawing.Imaging;
using System.IO;
using System.Linq;

namespace System.Drawing
{
    /// <summary>
    /// Specifies the rotation and flip to apply to an image. The values are the GDI+ ones:
    /// the low two bits carry the clockwise rotation in 90 degree steps, the third bit a
    /// horizontal flip, which is why several names share a value.
    /// </summary>
    public enum RotateFlipType
    {
        RotateNoneFlipNone = 0,
        Rotate90FlipNone = 1,
        Rotate180FlipNone = 2,
        Rotate270FlipNone = 3,
        RotateNoneFlipX = 4,
        Rotate90FlipX = 5,
        Rotate180FlipX = 6,
        Rotate270FlipX = 7,
        RotateNoneFlipY = Rotate180FlipX,
        Rotate90FlipY = Rotate270FlipX,
        Rotate180FlipY = RotateNoneFlipX,
        Rotate270FlipY = Rotate90FlipX,
        RotateNoneFlipXY = Rotate180FlipNone,
        Rotate90FlipXY = Rotate270FlipNone,
        Rotate180FlipXY = RotateNoneFlipNone,
        Rotate270FlipXY = Rotate90FlipNone
    }

    /// <summary>
    /// Base class for images
    /// </summary>
    public class Image : IDisposable
    {
        public virtual void Dispose() { }

        // Virtual so Bitmap can route these to its Mat. Non-virtual auto-properties here got
        // shadowed by Bitmap's expression-bodied Width/Height, and any access through an
        // Image-typed reference read the never-assigned auto-property backing field: 0.
        public virtual int Width { get; set; }
        public virtual int Height { get; set; }
        public ImageFormat RawFormat { get; set; }
    }

    /// <summary>
    /// Bitmap type that wraps OpenCV Mat
    /// </summary>
    public class Bitmap : Image
    {
        private Mat _mat;

        // The PixelFormat this bitmap was explicitly created with. The Mat type alone cannot
        // round-trip every creation format (Format32bppPArgb/Format32bppRgb both map to
        // CV_8UC4, Format16bppRgb565 to CV_16UC1), so LockBits' format check must compare
        // against this, not a format re-derived from the Mat. Undefined = derive from the Mat.
        private PixelFormat _createdFormat = PixelFormat.Undefined;

        public Bitmap()
        {
            _mat = new Mat();
        }

        public Bitmap(int width, int height)
        {
            _mat = new Mat(height, width, MatType.CV_8UC3);
        }

        public Bitmap(int width, int height, PixelFormat format)
        {
            _createdFormat = format;
            _mat = new Mat(height, width, ToMatType(format));
            // Initialize to zero
            _mat.SetTo(OpenCvSharp.Scalar.All(0));
        }

        /// <summary>
        /// Wraps an existing pixel buffer in place, mirroring
        /// Bitmap(int, int, int, PixelFormat, IntPtr). No copy is made: drawing into this Bitmap
        /// writes straight through to <paramref name="scan0"/>, which is the whole point of the
        /// overload - it is how a WriteableBitmap's back buffer gets rendered into with GDI+
        /// calls. The caller owns that memory and must keep it alive at least as long as this
        /// Bitmap.
        /// </summary>
        public Bitmap(int width, int height, int stride, PixelFormat format, IntPtr scan0)
        {
            if (scan0 == IntPtr.Zero)
            {
                throw new ArgumentException("Pixel buffer pointer must not be null.", nameof(scan0));
            }
            _createdFormat = format;
            _mat = Mat.FromPixelData(height, width, ToMatType(format), scan0, stride);
        }

        /// <summary>
        /// Maps a GDI+ pixel format onto the Mat element type that matches its memory layout.
        /// </summary>
        private static MatType ToMatType(PixelFormat format)
        {
            switch (format)
            {
                case PixelFormat.Format8bppIndexed:
                    return MatType.CV_8UC1;
                case PixelFormat.Format16bppGrayScale:
                    return MatType.CV_16UC1;
                case PixelFormat.Format24bppRgb:
                    return MatType.CV_8UC3;
                case PixelFormat.Format32bppArgb:
                case PixelFormat.Format32bppPArgb:
                case PixelFormat.Format32bppRgb:
                    return MatType.CV_8UC4;
                case PixelFormat.Format48bppRgb:
                    return MatType.CV_16UC3;
                case PixelFormat.Format16bppRgb565:
                    return MatType.CV_16UC1; // Store as 16-bit single channel
                default:
                    // Fail loudly rather than silently guessing an element size the caller's
                    // pixel math will not match (the LockBits stride/layout corruption class).
                    throw new NotSupportedException($"Bitmap creation with pixel format {format} is not supported.");
            }
        }

        public Bitmap(string filename)
        {
            _mat = Cv2.ImRead(filename, ImreadModes.Unchanged);
            if (_mat == null || _mat.Empty())
            {
                throw new ArgumentException($"Failed to load image from: {filename}");
            }
        }

        public Bitmap(Mat mat)
        {
            _mat = mat;
        }

        public Bitmap(Bitmap source)
        {
            _mat = source?._mat?.Clone() ?? new Mat();
            _createdFormat = source?._createdFormat ?? PixelFormat.Undefined;
        }

        public Bitmap(Bitmap source, int width, int height)
        {
            // Resize the source bitmap to the specified dimensions
            if (source == null || source._mat == null || source._mat.Empty())
            {
                _mat = new Mat();
                return;
            }

            _mat = new Mat();
            Cv2.Resize(source._mat, _mat, new OpenCvSharp.Size(width, height));
            _createdFormat = source._createdFormat;
        }

        public Bitmap Clone()
        {
            var clone = new Bitmap(_mat?.Clone());
            clone._createdFormat = _createdFormat;
            return clone;
        }

        /// <summary>
        /// Rotates and/or flips this bitmap in place, mirroring Bitmap.RotateFlip.
        /// The 90/270 degree cases swap width and height, so the backing Mat is replaced.
        /// </summary>
        public void RotateFlip(RotateFlipType rotateFlipType)
        {
            if (_mat == null || _mat.Empty()) return;

            // RotateFlipType packs a rotation in the low two bits and a flip in the next bit.
            int rotation = ((int)rotateFlipType) & 0x3;
            bool flipX = (((int)rotateFlipType) & 0x4) != 0;

            Mat result = _mat;
            bool resultCreated = false;

            if (rotation != 0)
            {
                var rotateCode = rotation switch
                {
                    1 => RotateFlags.Rotate90Clockwise,
                    2 => RotateFlags.Rotate180,
                    _ => RotateFlags.Rotate90Counterclockwise
                };
                result = new Mat();
                Cv2.Rotate(_mat, result, rotateCode);
                resultCreated = true;
            }

            if (flipX)
            {
                var flipped = new Mat();
                Cv2.Flip(result, flipped, FlipMode.Y);
                if (resultCreated)
                {
                    result.Dispose();
                }
                result = flipped;
                resultCreated = true;
            }

            if (resultCreated)
            {
                _mat.Dispose();
                _mat = result;
            }
        }

        // Getter-only overrides: the dimensions always come from the Mat, also when read
        // through an Image-typed reference. The inherited setters remain and are ignored.
        public override int Width => _mat.Width;
        public override int Height => _mat.Height;

        public PixelFormat PixelFormat
        {
            get
            {
                if (_mat == null || _mat.Empty()) return PixelFormat.Undefined;

                // Prefer the format this bitmap was explicitly created with - the Mat type is
                // lossy (PArgb/32bppRgb/565 have no distinct Mat representation).
                if (_createdFormat != PixelFormat.Undefined) return _createdFormat;

                var matType = _mat.Type();
                if (matType == MatType.CV_8UC1) return PixelFormat.Format8bppIndexed;
                if (matType == MatType.CV_16UC1) return PixelFormat.Format16bppGrayScale;
                if (matType == MatType.CV_8UC3) return PixelFormat.Format24bppRgb;
                if (matType == MatType.CV_8UC4) return PixelFormat.Format32bppArgb;
                if (matType == MatType.CV_16UC3) return PixelFormat.Format48bppRgb;

                return PixelFormat.Undefined;
            }
        }

        public Size Size => new Size(Width, Height);

        private Imaging.ColorPalette _palette;

        /// <summary>
        /// Gets or sets the color palette used for this Bitmap
        /// </summary>
        public Imaging.ColorPalette Palette
        {
            get
            {
                if (_palette != null)
                {
                    return _palette;
                }
                // Create a default grayscale palette for indexed images
                if (PixelFormat == PixelFormat.Format8bppIndexed)
                {
                    var palette = new Imaging.ColorPalette(256);
                    for (int i = 0; i < 256; i++)
                    {
                        palette.Entries[i] = Color.FromArgb(i, i, i);
                    }
                    return palette;
                }
                return new Imaging.ColorPalette();
            }
            set
            {
                _palette = value;
            }
        }

        /// <summary>
        /// Locks a bitmap into system memory
        /// </summary>
        public BitmapData LockBits(Rectangle rect, ImageLockMode mode, PixelFormat format)
        {
            // This shim always hands back the Mat's own full buffer/format; it cannot honor a
            // sub-rectangle crop or an on-the-fly format conversion the way real GDI+ LockBits does.
            // Silently ignoring either used to let callers walk the buffer with the wrong
            // stride/layout (REVIEW.md F4) - fail loudly instead so a real mismatch is caught at the
            // call site instead of silently corrupting pixel data.
            var fullRect = new Rectangle(0, 0, _mat.Width, _mat.Height);
            if (rect != fullRect)
            {
                throw new NotSupportedException(
                    $"LockBits does not support partial-rectangle locking; requested {rect} but bitmap is {fullRect}.");
            }
            if (format != this.PixelFormat)
            {
                throw new NotSupportedException(
                    $"LockBits does not support format conversion; requested {format} but bitmap is {this.PixelFormat}.");
            }

            // For OpenCV Mat, the data is already accessible
            // Return BitmapData pointing to the Mat's data
            return new BitmapData
            {
                Width = _mat.Width,
                Height = _mat.Height,
                Stride = (int)_mat.Step(),
                Scan0 = _mat.Data,
                PixelFormat = this.PixelFormat
            };
        }

        /// <summary>
        /// Unlocks a bitmap from system memory
        /// </summary>
        public void UnlockBits(BitmapData bitmapData)
        {
            // For OpenCV Mat, no action needed as data is always accessible
            // This is a no-op for compatibility
        }

        /// <summary>
        /// Gets the color of the specified pixel
        /// </summary>
        public Color GetPixel(int x, int y)
        {
            if (_mat == null || _mat.Empty())
            {
                throw new InvalidOperationException("Cannot get pixel from an empty bitmap");
            }
            if (x < 0 || x >= _mat.Width || y < 0 || y >= _mat.Height)
            {
                throw new ArgumentOutOfRangeException($"Pixel coordinates ({x}, {y}) are out of bounds");
            }

            var type = _mat.Type();
            if (type == MatType.CV_8UC3)
            {
                // BGR format
                var vec = _mat.Get<Vec3b>(y, x);
                return Color.FromArgb(255, vec.Item2, vec.Item1, vec.Item0); // BGR to RGB
            }
            else if (type == MatType.CV_8UC4)
            {
                // BGRA format
                var vec = _mat.Get<Vec4b>(y, x);
                return Color.FromArgb(vec.Item3, vec.Item2, vec.Item1, vec.Item0); // BGRA to ARGB
            }
            else if (type == MatType.CV_8UC1)
            {
                // Grayscale
                var value = _mat.Get<byte>(y, x);
                return Color.FromArgb(255, value, value, value);
            }
            else if (type == MatType.CV_16UC1)
            {
                // 16-bit grayscale - scale down to 8-bit
                var value = (byte)(_mat.Get<ushort>(y, x) >> 8);
                return Color.FromArgb(255, value, value, value);
            }
            else
            {
                throw new NotSupportedException($"GetPixel not supported for pixel format {PixelFormat}");
            }
        }

        /// <summary>
        /// Sets the color of the specified pixel
        /// </summary>
        public void SetPixel(int x, int y, Color color)
        {
            if (_mat == null || _mat.Empty())
            {
                throw new InvalidOperationException("Cannot set pixel in an empty bitmap");
            }
            if (x < 0 || x >= _mat.Width || y < 0 || y >= _mat.Height)
            {
                throw new ArgumentOutOfRangeException($"Pixel coordinates ({x}, {y}) are out of bounds");
            }

            var type = _mat.Type();
            if (type == MatType.CV_8UC3)
            {
                // BGR format
                _mat.Set(y, x, new Vec3b(color.B, color.G, color.R));
            }
            else if (type == MatType.CV_8UC4)
            {
                // BGRA format
                _mat.Set(y, x, new Vec4b(color.B, color.G, color.R, color.A));
            }
            else if (type == MatType.CV_8UC1)
            {
                // Grayscale - convert to luminance
                var luminance = (byte)(0.299 * color.R + 0.587 * color.G + 0.114 * color.B);
                _mat.Set(y, x, luminance);
            }
            else if (type == MatType.CV_16UC1)
            {
                // 16-bit grayscale - convert to luminance and scale up
                var luminance = (ushort)((0.299 * color.R + 0.587 * color.G + 0.114 * color.B) * 257);
                _mat.Set(y, x, luminance);
            }
            else
            {
                throw new NotSupportedException($"SetPixel not supported for pixel format {PixelFormat}");
            }
        }

        /// <summary>
        /// Saves the bitmap to a file
        /// </summary>
        public void Save(string filename)
        {
            if (_mat == null || _mat.Empty())
            {
                throw new InvalidOperationException("Cannot save an empty bitmap");
            }

            WriteFile(filename, null);
        }

        /// <summary>
        /// Saves the bitmap to a file with specified encoder and parameters
        /// </summary>
        public void Save(string filename, Imaging.ImageCodecInfo encoder, Imaging.EncoderParameters encoderParams)
        {
            if (_mat == null || _mat.Empty())
            {
                throw new InvalidOperationException("Cannot save an empty bitmap");
            }

            // For encoder-based save, use OpenCV's ImWrite which is more reliable on Linux
            int quality = QualityOf(encoderParams);

            // Determine extension from filename or encoder
            string extension = Path.GetExtension(filename).ToLowerInvariant();
            if (string.IsNullOrEmpty(extension))
            {
                // No extension in filename, determine from encoder
                if (encoder != null)
                {
                    if (encoder.FormatID == Imaging.ImageFormat.Jpeg.Guid)
                        extension = ".jpg";
                    else if (encoder.FormatID == Imaging.ImageFormat.Png.Guid)
                        extension = ".png";
                    else if (encoder.FilenameExtension != null)
                        extension = encoder.FilenameExtension.Split(';')[0]; // Take first extension
                }
                if (string.IsNullOrEmpty(extension))
                    extension = ".png"; // Default fallback
                    
                filename = Path.ChangeExtension(filename, extension);
            }

            WriteFile(filename, new[] { (int)ImwriteFlags.JpegQuality, quality });
        }

        /// <summary>
        /// Saves the bitmap to a file with specified format
        /// </summary>
        public void Save(string filename, Imaging.ImageFormat format)
        {
            if (_mat == null || _mat.Empty())
            {
                throw new InvalidOperationException("Cannot save an empty bitmap");
            }
            // OpenCV's ImWrite determines format from filename extension
            // Format parameter is for compatibility but not used
            WriteFile(filename, null);
        }

        /// <summary>
        /// Saves the bitmap to a stream with specified format
        /// </summary>
        public void Save(System.IO.Stream stream, Imaging.ImageFormat format)
        {
            if (_mat == null || _mat.Empty())
            {
                throw new InvalidOperationException("Cannot save an empty bitmap");
            }

            // Determine file extension from format
            string ext = ".png";
            if (format == Imaging.ImageFormat.Jpeg) ext = ".jpg";
            else if (format == Imaging.ImageFormat.Bmp) ext = ".bmp";
            else if (format == Imaging.ImageFormat.Tiff) ext = ".tiff";
            else if (format == Imaging.ImageFormat.Gif) ext = ".gif";

            byte[] buffer = EncodeToBuffer(ext, null);
            stream.Write(buffer, 0, buffer.Length);
        }

        /// <summary>
        /// Saves the bitmap to a stream with specified encoder and parameters
        /// </summary>
        public void Save(System.IO.Stream stream, Imaging.ImageCodecInfo encoder, Imaging.EncoderParameters encoderParams)
        {
            if (_mat == null || _mat.Empty())
            {
                throw new InvalidOperationException("Cannot save an empty bitmap");
            }

            // Determine file extension from codec
            string ext = ".png";
            if (encoder != null)
            {
                // Check by GUID first (more reliable)
                if (encoder.FormatID == Imaging.ImageFormat.Jpeg.Guid)
                {
                    ext = ".jpg";
                }
                else if (encoder.FormatID == Imaging.ImageFormat.Png.Guid)
                {
                    ext = ".png";
                }
                else if (encoder.FormatID == Imaging.ImageFormat.Bmp.Guid)
                {
                    ext = ".bmp";
                }
                else if (encoder.FormatID == Imaging.ImageFormat.Gif.Guid)
                {
                    ext = ".gif";
                }
                // Fallback to string matching if GUID didn't work
                else if (encoder.FormatDescription != null)
                {
                    if (encoder.FormatDescription.Contains("JPEG"))
                        ext = ".jpg";
                    else if (encoder.FormatDescription.Contains("PNG"))
                        ext = ".png";
                }
            }

            int quality = QualityOf(encoderParams);
            byte[] buffer = EncodeToBuffer(ext, new[] { (int)ImwriteFlags.JpegQuality, quality });
            stream.Write(buffer, 0, buffer.Length);
        }

        /// <summary>The JPEG quality among the encoder parameters (found by GDI+'s Quality GUID), else 95.</summary>
        private static int QualityOf(Imaging.EncoderParameters encoderParams)
        {
            var qualityParam = encoderParams?.Param?.FirstOrDefault(parameter =>
                parameter?.Encoder != null && parameter.Encoder.Guid == Imaging.Encoder.Quality.Guid);

            return qualityParam == null ? 95 : (int)Math.Clamp(qualityParam.Value, 0L, 100L);
        }

        /// <summary>Writes the file or throws as GDI+ does when it can't (OpenCV only returns false or throws its own).</summary>
        private void WriteFile(string filename, int[] parameters)
        {
            bool written;

            try
            {
                written = parameters == null
                    ? Cv2.ImWrite(filename, _mat)
                    : Cv2.ImWrite(filename, _mat, parameters);
            }
            catch (OpenCVException ex)
            {
                throw new System.Runtime.InteropServices.ExternalException($"A generic error occurred in GDI+. OpenCV could not write {filename}: {ex.Message}", ex);
            }

            if (!written)
            {
                throw new System.Runtime.InteropServices.ExternalException($"A generic error occurred in GDI+. OpenCV could not write {filename}.");
            }
        }

        private byte[] EncodeToBuffer(string extension, int[] parameters)
        {
            bool encoded;
            byte[] buffer;

            try
            {
                encoded = parameters == null
                    ? Cv2.ImEncode(extension, _mat, out buffer)
                    : Cv2.ImEncode(extension, _mat, out buffer, parameters);
            }
            catch (OpenCVException ex)
            {
                throw new System.Runtime.InteropServices.ExternalException($"A generic error occurred in GDI+. OpenCV could not encode the bitmap as {extension}: {ex.Message}", ex);
            }

            if (!encoded || buffer.Length == 0)
            {
                throw new System.Runtime.InteropServices.ExternalException($"A generic error occurred in GDI+. OpenCV could not encode the bitmap as {extension}.");
            }

            return buffer;
        }

        public override void Dispose() => _mat?.Dispose();

        // Implicit conversions
        // Returns internal Mat directly — in-place filter operations depend on this.
        // Use GetMat() when a safe clone with independent lifetime is needed.
        public static implicit operator Mat(Bitmap bmp) => bmp._mat;
        public static implicit operator Bitmap(Mat mat) => new Bitmap(mat);

        // Explicit method to get a cloned Mat
        public Mat GetMat() => _mat?.Clone();
    }

    /// <summary>
    /// Extension methods for System.Drawing types
    /// </summary>
    public static class DrawingExtensions
    {
        public static bool FullyInsideRect(this Rectangle lhs, Rectangle rhs)
        {
            // Top left of first rectangle starts outside of the other rectangle
            var rhsRightX = rhs.X + rhs.Width;
            var rhsBottomY = rhs.Y + rhs.Height;
            if (lhs.X < rhs.X || lhs.Y < rhs.Y || lhs.X >= rhsRightX || lhs.Y >= rhsBottomY)
            {
                return false;
            }

            // Now we know the top left corner is within the rectangle, so all we need to do is test the bottom-right corner
            var lhsRightX = lhs.X + lhs.Width;
            var lhsBottomY = lhs.Y + lhs.Height;
            return lhsRightX <= rhsRightX && lhsBottomY <= rhsBottomY;
        }
    }
}
