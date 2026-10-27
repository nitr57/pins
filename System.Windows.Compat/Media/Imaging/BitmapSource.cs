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

namespace System.Windows.Media.Imaging {
    // BitmapSource wraps OpenCvSharp.Mat for headless/OpenCV mode
    public class BitmapSource : ImageSource {
        protected Mat _mat;
        private bool _disposed;
        // Bytes of unmanaged memory reported to GC so it can schedule collections appropriately
        private long _memoryPressure;
        // Native handle of a Mat handed in through the public BitmapSource(Mat) constructor. Its
        // buffer may be caller-owned memory (Mat.FromPixelData) with no reference count, so it is
        // never shared (see TryShareMat). A handle, not a reference, so it keeps nothing alive; a
        // stale match can only ever disable sharing.
        private IntPtr _foreignMatPtr;

        public bool CanFreeze => true;

        /// <summary>
        /// WPF hangs a Dispatcher off every DispatcherObject and callers use it for thread
        /// affinity checks (VerifyAccess/CheckAccess). Headless has one logical dispatcher, so
        /// hand back the current one and those checks always pass.
        /// </summary>
        public System.Windows.Threading.Dispatcher Dispatcher => System.Windows.Threading.Dispatcher.CurrentDispatcher;

        public BitmapSource() {
            _mat = new Mat();
        }

        public BitmapSource(Mat source) : this(source, foreignMat: true) {
        }

        /// <summary>
        /// For Mats this assembly allocated itself (foreignMat: false), whose buffers OpenCV
        /// owns and reference-counts.
        /// </summary>
        internal BitmapSource(Mat source, bool foreignMat) {
            _mat = source;
            AddMemoryPressure();
            if (foreignMat && source != null) {
                _foreignMatPtr = source.CvPtr;
            }
        }

        // Constructor for cropping: new BitmapSource(mat, rectangle)
        public BitmapSource(Mat source, System.Drawing.Rectangle rect) {
            // Crop the source Mat using the rectangle
            var cvRect = new OpenCvSharp.Rect(rect.X, rect.Y, rect.Width, rect.Height);
            // Clone the ROI to avoid dangling reference to parent Mat
            // Creating Mat(source, cvRect) creates a view that references source.
            // If source is disposed, this view becomes invalid. Clone it instead.
            using (Mat roiMat = new Mat(source, cvRect)) {
                _mat = roiMat.Clone();
            }
            AddMemoryPressure();
        }

        protected void AddMemoryPressure() {
            if (_memoryPressure > 0) {
                GC.RemoveMemoryPressure(_memoryPressure);
                _memoryPressure = 0;
            }
            if (_mat != null && !_mat.Empty()) {
                _memoryPressure = (long)(_mat.Total() * _mat.ElemSize());
                if (_memoryPressure > 0)
                    GC.AddMemoryPressure(_memoryPressure);
            }
        }

        ~BitmapSource() {
            Dispose(false);
        }

        public override void Dispose() {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing) {
            if (_disposed) return;
            _disposed = true;
            if (_memoryPressure > 0) {
                GC.RemoveMemoryPressure(_memoryPressure);
                _memoryPressure = 0;
            }
            if (disposing) {
                _mat?.Dispose();
                _mat = null;
            }
        }

        public int PixelWidth => _mat.Width;
        public int PixelHeight => _mat.Height;
        public double DpiX { get; set; } = 96.0;
        public double DpiY { get; set; } = 96.0;

        public override double Width => _mat.Width;
        public override double Height => _mat.Height;

        public System.Windows.Media.PixelFormat Format {
            get {
                // Map OpenCV MatType to WPF PixelFormat
                if (_mat.Empty()) return System.Windows.Media.PixelFormats.Bgr24;

                var matType = _mat.Type();
                if (matType == MatType.CV_8UC1)
                    return System.Windows.Media.PixelFormats.Gray8;
                else if (matType == MatType.CV_16UC1)
                    return System.Windows.Media.PixelFormats.Gray16;
                else if (matType == MatType.CV_8UC3)
                    return System.Windows.Media.PixelFormats.Bgr24;
                else if (matType == MatType.CV_8UC4)
                    return System.Windows.Media.PixelFormats.Bgra32;
                else if (matType == MatType.CV_16UC3)
                    return System.Windows.Media.PixelFormats.Rgb48;
                else
                    return System.Windows.Media.PixelFormats.Bgr24;
            }
            set {
                // Optionally handle setting format (convert the Mat to the desired format)
                // This is rarely used in practice but may be needed for compatibility
            }
        }

        // Implicit conversions
        // Clone to prevent aliased ownership — caller owns the returned Mat
        public static implicit operator Mat(BitmapSource bmp) => bmp._mat?.Clone();
        public static implicit operator BitmapSource(Mat mat) => new BitmapSource(mat);

        /// <summary>
        /// Read access to the pixel Mat for code in this assembly, without the full-image copy
        /// the implicit conversion above makes. Disposing the lease only disposes a Mat the lease
        /// cloned itself. WriteableBitmap and RenderTargetBitmap are still cloned, because their
        /// pixels can be written (BackBuffer, Render) while the caller is reading.
        /// </summary>
        internal MatLease LeaseMat() {
            if (this is WriteableBitmap || this is RenderTargetBitmap) {
                return new MatLease(_mat?.Clone(), owned: true);
            }
            return new MatLease(_mat, owned: false);
        }

        /// <summary>
        /// A new Mat header over this bitmap's pixel buffer, sharing it instead of copying it, or
        /// null when sharing is not safe. Only a frozen bitmap qualifies, and never one whose
        /// pixels can still be written in place (WriteableBitmap, RenderTargetBitmap). The buffer
        /// must be OpenCV-owned, so its reference count keeps it alive after this bitmap is
        /// disposed, and continuous, so the header covers exactly the image and nothing more.
        /// </summary>
        internal Mat TryShareMat() {
            if (!IsFrozen || this is WriteableBitmap || this is RenderTargetBitmap) {
                return null;
            }
            Mat mat = _mat;
            if (mat == null || mat.CvPtr == _foreignMatPtr || mat.Empty() || mat.Dims != 2 || !mat.IsContinuous()) {
                return null;
            }
            return new Mat(mat, new OpenCvSharp.Rect(0, 0, mat.Cols, mat.Rows));
        }

        /// <summary>
        /// An unfrozen deep copy, as in WPF.
        /// </summary>
        public BitmapSource Clone() {
            return new BitmapSource(_mat.Clone(), foreignMat: false) { DpiX = DpiX, DpiY = DpiY };
        }

        public override void Freeze() {
            // In WPF, Freeze makes objects immutable for thread safety
            // For OpenCV Mat, this is a no-op since Mat is already thread-safe for reading
            base.Freeze();
        }

        public void CopyPixels(byte[] pixels, int stride, int offset) {
            CopyPixelsToArray(pixels, stride, offset, sizeof(byte));
        }

        public void CopyPixels(ushort[] pixels, int stride, int offset) {
            CopyPixelsToArray(pixels, stride, offset, sizeof(ushort));
        }

        /// <summary>
        /// Copies the whole image into a byte[] or ushort[], matching WPF: offset is an element
        /// index into pixels and stride is the destination row pitch in bytes (0 packs the rows).
        /// The pixel bytes are copied as they are, whatever the array's element type.
        /// </summary>
        private unsafe void CopyPixelsToArray(Array pixels, int stride, int offset, int elementSize) {
            ArgumentNullException.ThrowIfNull(pixels);

            if (_mat.Empty()) {
                return;
            }

            if (offset < 0) {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }

            long rowBytes = (long)_mat.Cols * _mat.ElemSize();
            long pitch = stride == 0 ? rowBytes : stride;
            if (pitch < rowBytes) {
                throw new ArgumentOutOfRangeException(nameof(stride), $"Stride {stride} is smaller than a row of {rowBytes} bytes");
            }

            long byteOffset = (long)offset * elementSize;
            long available = Buffer.ByteLength(pixels);
            long needed = byteOffset + pitch * (_mat.Rows - 1) + rowBytes;
            if (available < needed) {
                throw new ArgumentException($"Destination array too small. Need {needed} bytes, got {available}");
            }

            fixed (byte* destination = &System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference(pixels)) {
                byte* source = (byte*)_mat.Data;
                long sourceStep = _mat.Step();

                if (_mat.IsContinuous() && pitch == rowBytes) {
                    Buffer.MemoryCopy(source, destination + byteOffset, available - byteOffset, rowBytes * _mat.Rows);
                } else {
                    // A non-continuous Mat (e.g. an un-cloned ROI view) has padding between rows, and
                    // the destination can have its own; copy row by row with each side's pitch.
                    for (int y = 0; y < _mat.Rows; y++) {
                        long destinationOffset = byteOffset + y * pitch;
                        Buffer.MemoryCopy(source + y * sourceStep, destination + destinationOffset, available - destinationOffset, rowBytes);
                    }
                }
            }
            GC.KeepAlive(this);
        }

        public void CopyPixels(Array pixels, int stride, int offset) {
            // Generic CopyPixels that dispatches to specific type
            ArgumentNullException.ThrowIfNull(pixels);

            if (pixels is byte[] byteArray) {
                CopyPixels(byteArray, stride, offset);
            } else if (pixels is ushort[] ushortArray) {
                CopyPixels(ushortArray, stride, offset);
            } else {
                throw new NotSupportedException($"Array type {pixels.GetType()} not supported");
            }
        }

        /// <summary>
        /// Copies sourceRect into a managed array, matching WPF: offset is an element index into
        /// pixels, and stride is the destination pitch in bytes.
        /// </summary>
        public void CopyPixels(Int32Rect sourceRect, Array pixels, int stride, int offset) {
            ArgumentNullException.ThrowIfNull(pixels);
            int elementSize = Runtime.InteropServices.Marshal.SizeOf(pixels.GetType().GetElementType());
            int byteOffset = checked(offset * elementSize);
            int bufferSize = Buffer.ByteLength(pixels) - byteOffset;
            if (offset < 0 || bufferSize < 0) {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }
            Runtime.InteropServices.GCHandle handle = Runtime.InteropServices.GCHandle.Alloc(pixels, Runtime.InteropServices.GCHandleType.Pinned);
            try {
                CopyPixels(sourceRect, handle.AddrOfPinnedObject() + byteOffset, bufferSize, stride);
            } finally {
                handle.Free();
            }
        }

        public void CopyPixels(Int32Rect sourceRect, IntPtr buffer, int bufferSize, int stride) {
            // Copy Mat data to a buffer pointer
            if (_mat.Empty()) return;

            // If sourceRect is empty, copy the entire image
            int srcX = sourceRect.IsEmpty ? 0 : sourceRect.X;
            int srcY = sourceRect.IsEmpty ? 0 : sourceRect.Y;
            int width = sourceRect.IsEmpty ? _mat.Width : sourceRect.Width;
            int height = sourceRect.IsEmpty ? _mat.Height : sourceRect.Height;

            // Ensure we don't exceed bounds
            width = Math.Min(width, _mat.Width - srcX);
            height = Math.Min(height, _mat.Height - srcY);

            // Copy the data
            if (sourceRect.IsEmpty && stride == _mat.Step()) {
                // Fast path: copy entire image in one go
                int dataSize = (int)(_mat.Step() * _mat.Height);
                unsafe {
                    Buffer.MemoryCopy(_mat.Data.ToPointer(), buffer.ToPointer(), bufferSize, Math.Min(dataSize, bufferSize));
                }
            } else {
                // Copy row by row
                int rowBytes = Math.Min(width * _mat.ElemSize(), stride);
                for (int y = 0; y < height; y++) {
                    IntPtr srcPtr = _mat.Data + ((srcY + y) * (int)_mat.Step()) + (srcX * _mat.ElemSize());
                    IntPtr dstPtr = buffer + (y * stride);
                    unsafe {
                        Buffer.MemoryCopy(srcPtr.ToPointer(), dstPtr.ToPointer(), bufferSize - (y * stride), rowBytes);
                    }
                }
            }
            // Prevent the GC from collecting this BitmapSource (and its _mat) while
            // native pointers extracted from _mat.Data are still in use above.
            GC.KeepAlive(this);
        }

        public static BitmapSource Create(int pixelWidth, int pixelHeight, double dpiX, double dpiY,
            System.Windows.Media.PixelFormat pixelFormat, BitmapPalette palette, IntPtr buffer, int bufferSize, int stride) {
            // Determine the OpenCV Mat type from the pixel format
            MatType matType = pixelFormat;

            // Create a Mat that owns its data, not just a view
            // Mat.FromPixelData creates a view that references the buffer, which may become invalid
            Mat mat = new Mat(pixelHeight, pixelWidth, matType);

            // Always copy row by row to properly handle stride mismatches and buffer bounds.
            // When a bitmap is locked with one format but converted to another, stride calculations
            // can mismatch, causing buffer overruns. This approach is safe against all variants.
            int rowBytes = (int)System.Math.Min((long)stride, mat.Step());
            int maxRowsToCopy = bufferSize / (stride > 0 ? stride : 1);
            int rowsToCopy = System.Math.Min(pixelHeight, maxRowsToCopy);

            for (int y = 0; y < rowsToCopy; y++) {
                IntPtr srcPtr = buffer + (y * stride);
                IntPtr dstPtr = mat.Data + (y * (int)mat.Step());
                unsafe {
                    System.Buffer.MemoryCopy(srcPtr.ToPointer(), dstPtr.ToPointer(), mat.Step(), rowBytes);
                }
            }

            return new BitmapSource(mat, foreignMat: false);
        }

        public static BitmapSource Create(int pixelWidth, int pixelHeight, double dpiX, double dpiY,
            System.Windows.Media.PixelFormat pixelFormat, BitmapPalette palette, Array pixels, int stride) {
            ArgumentNullException.ThrowIfNull(pixels);

            // Determine the OpenCV Mat type from the pixel format
            MatType matType = pixelFormat;

            // Create an empty Mat with the specified dimensions
            Mat mat = new Mat(pixelHeight, pixelWidth, matType);
            try {
                CopyArrayToMat(pixels, stride, mat);
            } catch {
                mat.Dispose();
                throw;
            }

            return new BitmapSource(mat, foreignMat: false);
        }

        /// <summary>
        /// Copies a managed pixel array into a new Mat, matching WPF: stride is the source row pitch
        /// in bytes (0 packs the rows), and the array must hold every row the stride implies.
        /// </summary>
        private static unsafe void CopyArrayToMat(Array pixels, int stride, Mat mat) {
            long rowBytes = (long)mat.Cols * mat.ElemSize();
            long pitch = stride == 0 ? rowBytes : stride;
            if (pitch < rowBytes) {
                throw new ArgumentOutOfRangeException(nameof(stride), $"Stride {stride} is smaller than a row of {rowBytes} bytes");
            }

            // Buffer.ByteLength is the managed size and rejects arrays of non-primitive types
            long available = Buffer.ByteLength(pixels);
            long needed = mat.Rows == 0 ? 0 : pitch * (mat.Rows - 1) + rowBytes;
            if (available < needed) {
                throw new ArgumentException($"Pixel array too small. Need {needed} bytes, got {available}", nameof(pixels));
            }

            fixed (byte* source = &System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference(pixels)) {
                byte* destination = (byte*)mat.Data;
                long destinationStep = mat.Step();

                if (pitch == rowBytes && destinationStep == rowBytes) {
                    Buffer.MemoryCopy(source, destination, destinationStep * mat.Rows, needed);
                } else {
                    for (int y = 0; y < mat.Rows; y++) {
                        Buffer.MemoryCopy(source + y * pitch, destination + y * destinationStep, destinationStep, rowBytes);
                    }
                }
            }
        }
    }

    /// <summary>
    /// A Mat handed out for reading (see BitmapSource.LeaseMat). Disposing it only disposes a
    /// Mat the lease cloned itself.
    /// </summary>
    internal readonly struct MatLease : IDisposable {
        private readonly bool _owned;

        public MatLease(Mat mat, bool owned) {
            Mat = mat;
            _owned = owned;
        }

        /// <summary>
        /// Read-only: never write to it and never dispose it directly.
        /// </summary>
        public Mat Mat { get; }

        public void Dispose() {
            if (_owned) {
                Mat?.Dispose();
            }
        }
    }

    public class BitmapPalette {
        public BitmapPalette(System.Collections.Generic.List<System.Windows.Media.Color> colors) { }
    }

    public static class BitmapPalettes {
        private static BitmapPalette _gray256;

        public static BitmapPalette Gray256 {
            get {
                if (_gray256 == null) {
                    var colors = new System.Collections.Generic.List<System.Windows.Media.Color>(256);
                    for (int i = 0; i < 256; i++) {
                        byte value = (byte)i;
                        colors.Add(System.Windows.Media.Color.FromRgb(value, value, value));
                    }
                    _gray256 = new BitmapPalette(colors);
                }
                return _gray256;
            }
        }
    }

    public class WriteableBitmap : BitmapSource {
        public WriteableBitmap(int pixelWidth, int pixelHeight, double dpiX, double dpiY,
            System.Windows.Media.PixelFormat pixelFormat, BitmapPalette palette) : base() {
            // Create a new Mat with the specified dimensions and format
            MatType matType = pixelFormat;
            _mat.Dispose(); // the empty Mat base() created
            _mat = new Mat(pixelHeight, pixelWidth, matType);
            AddMemoryPressure();
        }

        /// <summary>
        /// Pointer to the bitmap's pixel buffer. The backing Mat's data block *is* the back
        /// buffer - there is no separate front buffer to flip to - so writes through this
        /// pointer are visible immediately and <see cref="Lock"/>/<see cref="Unlock"/>/
        /// <see cref="AddDirtyRect"/> have nothing to synchronize or invalidate.
        /// </summary>
        public IntPtr BackBuffer => _mat.Data;

        public int BackBufferStride => (int)_mat.Step();

        /// <summary>
        /// No-op: see <see cref="BackBuffer"/>. Kept so callers can follow WPF's mandatory
        /// Lock/write/AddDirtyRect/Unlock protocol unchanged.
        /// </summary>
        public void Lock() { }

        public void Unlock() { }

        public void AddDirtyRect(Int32Rect dirtyRect) { }

        public WriteableBitmap(BitmapSource source) : base() {
            // Implicit operator already clones — take ownership directly
            if (source != null) {
                Mat sourceMat = (Mat)source;
                if (sourceMat != null && !sourceMat.Empty()) {
                    _mat.Dispose(); // the empty Mat base() created
                    _mat = sourceMat;
                } else {
                    // Keep the empty Mat base() created
                    sourceMat?.Dispose();
                }
            }
            AddMemoryPressure();
        }
    }

    public class BitmapImage : BitmapSource {
        public BitmapImage() : base() { }
        public BitmapImage(Uri uriSource) : base() {
            UriSource = uriSource;
            LoadFromUri();
        }

        public Uri UriSource { get; set; }
        public System.IO.Stream StreamSource { get; set; }
        public BitmapCacheOption CacheOption { get; set; }

        /// <summary>
        /// Accepted and ignored. The options WPF exposes here (PreservePixelFormat, DelayCreation,
        /// IgnoreColorProfile, ...) describe WIC decoder behavior that has no counterpart in the
        /// OpenCV decode path this shim uses.
        /// </summary>
        public BitmapCreateOptions CreateOptions { get; set; }

        /// <summary>
        /// Requested decode width in pixels, or 0 for the image's natural size. OpenCV has no
        /// scale-on-decode, so this downscales after the fact - which still delivers the point of
        /// the hint (a large source image does not stay resident at full size) even though the
        /// decode itself is not cheaper.
        /// </summary>
        public int DecodePixelWidth { get; set; }

        public int DecodePixelHeight { get; set; }

        public void BeginInit() { }
        public void EndInit() {
            // Load from StreamSource if set, otherwise fall back to UriSource - WPF supports
            // both `new BitmapImage(uri)` and the BeginInit/UriSource=.../EndInit idiom.
            if (StreamSource != null) {
                SetSource(StreamSource);
            } else if (UriSource != null) {
                LoadFromUri();
            }
            ApplyDecodeSize();
        }

        private void LoadFromUri() {
            if (UriSource == null) {
                return;
            }

            try {
                string path = UriSource.IsAbsoluteUri && UriSource.IsFile ? UriSource.LocalPath : UriSource.ToString();
                var mat = OpenCvSharp.Cv2.ImRead(path, OpenCvSharp.ImreadModes.Unchanged);
                if (mat != null && !mat.Empty()) {
                    _mat?.Dispose();
                    _mat = mat;
                    AddMemoryPressure();
                } else {
                    mat?.Dispose();
                }
            } catch {
                // Malformed/unsupported URI - leave the image empty, matching SetSource's
                // silent-failure behavior on a bad stream.
            }
        }

        /// <summary>
        /// Resizes the loaded image to DecodePixelWidth/DecodePixelHeight. Matching WPF, leaving
        /// one of the two at 0 derives it from the other so the aspect ratio is preserved, and
        /// leaving both at 0 keeps the natural size.
        /// </summary>
        private void ApplyDecodeSize() {
            if (DecodePixelWidth <= 0 && DecodePixelHeight <= 0) {
                return;
            }
            if (_mat == null || _mat.Empty()) {
                return;
            }

            int width = DecodePixelWidth;
            int height = DecodePixelHeight;
            if (width <= 0) {
                width = (int)Math.Round((double)height * _mat.Width / _mat.Height);
            } else if (height <= 0) {
                height = (int)Math.Round((double)width * _mat.Height / _mat.Width);
            }
            if (width <= 0 || height <= 0 || (width == _mat.Width && height == _mat.Height)) {
                return;
            }

            var resized = new OpenCvSharp.Mat();
            // Area is the right filter for the shrink this is almost always used for, and
            // degrades to a usable bilinear-ish result on the rare upscale.
            OpenCvSharp.Cv2.Resize(_mat, resized, new OpenCvSharp.Size(width, height), 0, 0, OpenCvSharp.InterpolationFlags.Area);
            _mat.Dispose();
            _mat = resized;
            AddMemoryPressure();
        }

        public void SetSource(System.IO.Stream stream) {
            // Load image from stream using OpenCV
            if (stream != null && stream.CanSeek) {
                stream.Position = 0;
                var bytes = new byte[stream.Length];
                int bytesRead = 0;
                int offset = 0;
                while (offset < bytes.Length && (bytesRead = stream.Read(bytes, offset, bytes.Length - offset)) > 0) {
                    offset += bytesRead;
                }
                var mat = OpenCvSharp.Cv2.ImDecode(bytes, OpenCvSharp.ImreadModes.Unchanged);
                if (mat != null && !mat.Empty()) {
                    _mat?.Dispose();
                    _mat = mat;
                    AddMemoryPressure();
                } else {
                    mat?.Dispose();
                }
            }
        }
    }

    public enum BitmapCacheOption {
        Default = 0,
        OnDemand = 0,
        OnLoad = 1,
        None = 2
    }

    /// <summary>
    /// RenderTargetBitmap renders visual content into a bitmap
    /// </summary>
    public class RenderTargetBitmap : BitmapSource {
        public RenderTargetBitmap(int pixelWidth, int pixelHeight, double dpiX, double dpiY,
            System.Windows.Media.PixelFormat pixelFormat) : base() {
            // Create the target Mat based on pixel format
            MatType matType = pixelFormat;
            _mat.Dispose(); // the empty Mat base() created
            _mat = new Mat(pixelHeight, pixelWidth, matType);

            // Initialize with transparent/black background
            if (matType == MatType.CV_8UC4) {
                _mat.SetTo(new OpenCvSharp.Scalar(0, 0, 0, 0)); // Transparent
            } else {
                _mat.SetTo(OpenCvSharp.Scalar.All(0)); // Black
            }
            AddMemoryPressure();
        }

        /// <summary>
        /// Renders a DrawingVisual onto this bitmap
        /// </summary>
        /// <summary>
        /// Renders any visual, matching WPF's Render(Visual). Only a DrawingVisual carries
        /// recorded drawing operations in this shim - a laid-out element tree has no headless
        /// render pass behind it - so anything else leaves the target untouched.
        /// </summary>
        public void Render(System.Windows.Media.Visual visual) {
            if (visual is System.Windows.Media.DrawingVisual drawingVisual) {
                Render(drawingVisual);
            }
        }

        public void Render(System.Windows.Media.DrawingVisual visual) {
            if (visual == null || _mat == null || _mat.Empty()) {
                return;
            }

            // Process each drawing operation
            foreach (var operation in visual.Operations) {
                switch (operation.Type) {
                    case System.Windows.Media.DrawingOperation.OperationType.DrawImage:
                        RenderDrawImage(operation);
                        break;
                    case System.Windows.Media.DrawingOperation.OperationType.DrawLine:
                        RenderDrawLine(operation);
                        break;
                    case System.Windows.Media.DrawingOperation.OperationType.DrawRectangle:
                        RenderDrawRectangle(operation);
                        break;
                    case System.Windows.Media.DrawingOperation.OperationType.DrawText:
                        RenderDrawText(operation);
                        break;
                    case System.Windows.Media.DrawingOperation.OperationType.DrawGeometry:
                        RenderGeometry(operation.Geometry, operation.Brush, operation.Pen);
                        break;
                }
            }
        }

        private void RenderDrawImage(System.Windows.Media.DrawingOperation operation) {
            if (operation.Image == null) return;

            using var sourceLease = operation.Image.LeaseMat();
            Mat sourceMat = sourceLease.Mat;
            if (sourceMat == null || sourceMat.Empty()) return;

            int x = (int)operation.Rect.X;
            int y = (int)operation.Rect.Y;
            int width = (int)operation.Rect.Width;
            int height = (int)operation.Rect.Height;
            if (width <= 0 || height <= 0) return;

            // Clip to the canvas instead of rejecting the whole draw when the requested rect is
            // only partially out of bounds (e.g. a thumbnail drawn slightly past the canvas edge).
            int clippedX = System.Math.Max(0, x);
            int clippedY = System.Math.Max(0, y);
            int clippedRight = System.Math.Min(_mat.Width, x + width);
            int clippedBottom = System.Math.Min(_mat.Height, y + height);
            int clippedWidth = clippedRight - clippedX;
            int clippedHeight = clippedBottom - clippedY;
            if (clippedWidth <= 0 || clippedHeight <= 0) return; // entirely outside the canvas

            Mat resizedSource = sourceMat;
            if (sourceMat.Width != width || sourceMat.Height != height) {
                resizedSource = new Mat();
                OpenCvSharp.Cv2.Resize(sourceMat, resizedSource, new OpenCvSharp.Size(width, height));
            }

            // If clipping trimmed any edge, crop the resized source down to the visible sub-rect
            // rather than drawing the whole (now mis-sized) resized image at the clipped position.
            int srcOffsetX = clippedX - x;
            int srcOffsetY = clippedY - y;
            Mat visibleSource = resizedSource;
            bool visibleSourceCreated = false;
            if (srcOffsetX != 0 || srcOffsetY != 0 || clippedWidth != resizedSource.Width || clippedHeight != resizedSource.Height) {
                using var srcRoi = new Mat(resizedSource, new OpenCvSharp.Rect(srcOffsetX, srcOffsetY, clippedWidth, clippedHeight));
                visibleSource = srcRoi.Clone();
                visibleSourceCreated = true;
            }

            try {
                Mat convertedSource = visibleSource;
                if (visibleSource.Type() != _mat.Type()) {
                    convertedSource = new Mat();
                    if (visibleSource.Channels() == 1 && _mat.Channels() == 4) {
                        OpenCvSharp.Cv2.CvtColor(visibleSource, convertedSource, OpenCvSharp.ColorConversionCodes.GRAY2BGRA);
                    } else if (visibleSource.Channels() == 3 && _mat.Channels() == 4) {
                        OpenCvSharp.Cv2.CvtColor(visibleSource, convertedSource, OpenCvSharp.ColorConversionCodes.BGR2BGRA);
                    } else if (visibleSource.Channels() == 4 && _mat.Channels() == 3) {
                        OpenCvSharp.Cv2.CvtColor(visibleSource, convertedSource, OpenCvSharp.ColorConversionCodes.BGRA2BGR);
                    } else {
                        visibleSource.ConvertTo(convertedSource, _mat.Depth());
                    }
                }

                var roi = new OpenCvSharp.Rect(clippedX, clippedY, clippedWidth, clippedHeight);
                using (var targetRoi = new Mat(_mat, roi)) {
                    convertedSource.CopyTo(targetRoi);
                }

                if (convertedSource != visibleSource) {
                    convertedSource.Dispose();
                }
            } finally {
                if (visibleSourceCreated) {
                    visibleSource.Dispose();
                }
                if (resizedSource != sourceMat) {
                    resizedSource.Dispose();
                }
            }
        }

        // REVIEW.md F12: DrawGeometry operations were silently skipped, so any annotation drawn
        // via a Geometry rather than a plain rect/line vanished from the rendered bitmap.
        // RectangleGeometry/PathGeometry/GeometryGroup carry actual geometric data in this compat
        // layer and are rendered here; EllipseGeometry/LineGeometry currently have no data
        // properties at all in this codebase, so there is nothing yet to draw for those.
        private void RenderGeometry(System.Windows.Media.Geometry geometry, System.Windows.Media.Brush brush, System.Windows.Media.Pen pen) {
            if (geometry == null) return;

            switch (geometry) {
                case System.Windows.Media.RectangleGeometry rectGeometry: {
                    var r = rectGeometry.Rect;
                    var cvRect = new OpenCvSharp.Rect((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height);
                    if (brush != null) {
                        OpenCvSharp.Cv2.Rectangle(_mat, cvRect, GetScalarFromBrush(brush), -1);
                    }
                    if (pen != null) {
                        OpenCvSharp.Cv2.Rectangle(_mat, cvRect, GetScalarFromBrush(pen.Brush), System.Math.Max(1, (int)pen.Thickness));
                    }
                    break;
                }
                case System.Windows.Media.PathGeometry pathGeometry: {
                    foreach (var figure in pathGeometry.Figures) {
                        var points = new System.Collections.Generic.List<OpenCvSharp.Point> {
                            new OpenCvSharp.Point((int)figure.StartPoint.X, (int)figure.StartPoint.Y)
                        };
                        foreach (var segment in figure.Segments) {
                            if (segment is System.Windows.Media.LineSegment lineSegment) {
                                points.Add(new OpenCvSharp.Point((int)lineSegment.Point.X, (int)lineSegment.Point.Y));
                            }
                        }
                        if (points.Count < 2) continue;

                        var ptArray = points.ToArray();
                        if (brush != null) {
                            OpenCvSharp.Cv2.FillPoly(_mat, new[] { ptArray }, GetScalarFromBrush(brush));
                        }
                        if (pen != null) {
                            OpenCvSharp.Cv2.Polylines(_mat, new[] { ptArray }, figure.IsClosed, GetScalarFromBrush(pen.Brush), System.Math.Max(1, (int)pen.Thickness));
                        }
                    }
                    break;
                }
                case System.Windows.Media.GeometryGroup group: {
                    foreach (var child in group.Children) {
                        RenderGeometry(child, brush, pen);
                    }
                    break;
                }
            }
        }

        private static OpenCvSharp.Scalar GetScalarFromBrush(System.Windows.Media.Brush brush) {
            if (brush is System.Windows.Media.SolidColorBrush scb) {
                var c = scb.Color;
                return new OpenCvSharp.Scalar(c.B, c.G, c.R, c.A);
            }
            return new OpenCvSharp.Scalar(255, 255, 255, 255);
        }

        private void RenderDrawLine(System.Windows.Media.DrawingOperation operation) {
            if (operation.Pen == null) return;
            var color = GetScalarFromBrush(operation.Pen.Brush);
            int thickness = System.Math.Max(1, (int)operation.Pen.Thickness);
            OpenCvSharp.Cv2.Line(_mat,
                new OpenCvSharp.Point((int)operation.Point1.X, (int)operation.Point1.Y),
                new OpenCvSharp.Point((int)operation.Point2.X, (int)operation.Point2.Y),
                color, thickness);
        }

        private void RenderDrawRectangle(System.Windows.Media.DrawingOperation operation) {
            var rect = new OpenCvSharp.Rect(
                (int)operation.Rect.X, (int)operation.Rect.Y,
                (int)operation.Rect.Width, (int)operation.Rect.Height);
            if (operation.Brush != null) {
                var fillColor = GetScalarFromBrush(operation.Brush);
                OpenCvSharp.Cv2.Rectangle(_mat, rect, fillColor, -1);
            }
            if (operation.Pen != null) {
                var penColor = GetScalarFromBrush(operation.Pen.Brush);
                int thickness = System.Math.Max(1, (int)operation.Pen.Thickness);
                OpenCvSharp.Cv2.Rectangle(_mat, rect, penColor, thickness);
            }
        }

        private void RenderDrawText(System.Windows.Media.DrawingOperation operation) {
            if (operation.FormattedText == null) return;
            string text = operation.FormattedText.Text ?? "";
            var color = GetScalarFromBrush(operation.FormattedText.Foreground);
            double fontScale = operation.FormattedText.FontSize / 20.0;
            OpenCvSharp.Cv2.PutText(_mat, text,
                new OpenCvSharp.Point((int)operation.Point1.X, (int)operation.Point1.Y),
                OpenCvSharp.HersheyFonts.HersheySimplex, fontScale, color, 1);
        }
    }
}
