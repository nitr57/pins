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
using System.Windows.Media;

namespace System.Windows.Media.Imaging {
    /// <summary>
    /// TransformedBitmap - applies a transform to a bitmap using OpenCV
    /// </summary>
    public class TransformedBitmap : BitmapSource {
        private BitmapSource _source;
        private Transform _transform;

        public TransformedBitmap() : base() {
        }

        public TransformedBitmap(BitmapSource source, Transform transform) : base() {
            _source = source;
            _transform = transform;
            _mat?.Dispose(); // dispose the empty Mat created by base()
            _mat = null;
            ApplyTransform();
        }

        public BitmapSource Source {
            get => _source;
            set => _source = value;
        }

        public Transform Transform {
            get => _transform;
            set => _transform = value;
        }

        public void BeginInit() {
            // Initialization started
        }

        public void EndInit() {
            // Initialization complete - apply the transform
            ApplyTransform();
        }

        private void ApplyTransform() {
            if (_source == null || _transform == null) {
                // REVIEW.md F12: the 2-arg constructor nulls _mat before calling this method, so
                // bailing out here with a null Source/Transform (e.g. EndInit() called before
                // both are set) must not leave _mat null - that NREs on the next PixelWidth/etc.
                if (_mat == null) {
                    _mat = new Mat();
                }
                return;
            }

            using var sourceLease = _source.LeaseMat();
            Mat sourceMat = sourceLease.Mat;
            Mat result = new Mat();

            // Apply the transform
            if (_transform is ScaleTransform scaleTransform) {
                double scaleX = scaleTransform.ScaleX;
                double scaleY = scaleTransform.ScaleY;

                // Reject invalid scale values (NaN, Infinity) and no-op zero scales
                if (double.IsNaN(scaleX) || double.IsInfinity(scaleX) || double.IsNaN(scaleY) || double.IsInfinity(scaleY)) {
                    // Do not attempt to resize with invalid factors; copy the source
                    sourceMat.CopyTo(result);
                    SetResult(result);
                    return;
                }

                if (scaleX == 0.0 || scaleY == 0.0) {
                    // A scale of zero is effectively an invalid resize; copy the source
                    sourceMat.CopyTo(result);
                    SetResult(result);
                    return;
                }

                // Determine new dimensions; ensure at least 1 pixel in each dimension
                int newWidth = Math.Max(1, (int)Math.Round(sourceMat.Width * Math.Abs(scaleX)));
                int newHeight = Math.Max(1, (int)Math.Round(sourceMat.Height * Math.Abs(scaleY)));

                var newSize = new OpenCvSharp.Size(newWidth, newHeight);

                // Resize first (using absolute scale), then flip if needed for negative scales
                if (scaleX < 0 || scaleY < 0) {
                    using var resized = new Mat();
                    Cv2.Resize(sourceMat, resized, newSize, 0, 0, InterpolationFlags.Linear);

                    // REVIEW.md F25: OpenCV's FlipMode.X flips around the x-axis (vertical
                    // mirror), and FlipMode.Y flips around the y-axis (horizontal mirror) - the
                    // opposite of what the names suggest at a glance.
                    FlipMode flipMode = FlipMode.Y; // negative ScaleX -> horizontal mirror
                    if (scaleX < 0 && scaleY < 0) {
                        flipMode = FlipMode.XY; // Flip both
                    } else if (scaleY < 0) {
                        flipMode = FlipMode.X; // negative ScaleY -> vertical mirror
                    }
                    Cv2.Flip(resized, result, flipMode);
                } else {
                    Cv2.Resize(sourceMat, result, newSize, 0, 0, InterpolationFlags.Linear);
                }
            } else if (_transform is TranslateTransform translateTransform) {
                // Shift the raster within the same-sized canvas; content shifted past an edge is
                // clipped, and the newly-exposed area is filled with black (BorderTypes.Constant).
                double dx = translateTransform.X;
                double dy = translateTransform.Y;
                using var translation = new Mat(2, 3, MatType.CV_64F);
                translation.Set(0, 0, 1.0); translation.Set(0, 1, 0.0); translation.Set(0, 2, dx);
                translation.Set(1, 0, 0.0); translation.Set(1, 1, 1.0); translation.Set(1, 2, dy);
                Cv2.WarpAffine(sourceMat, result, translation, sourceMat.Size(), InterpolationFlags.Linear, BorderTypes.Constant, Scalar.All(0));
            } else {
                // General affine fallback for every other transform (RotateTransform,
                // TransformGroup, MatrixTransform, custom subclasses): map the WPF row-vector
                // matrix (x' = x*M11 + y*M21 + OffsetX) to OpenCV's column convention, size the
                // output to the transformed bounding box, and re-origin the content into it -
                // matching WPF, which never leaves the transformed bitmap partially off-canvas.
                var m = _transform.Value;
                double m00 = m.M11, m01 = m.M21, m02 = m.OffsetX;
                double m10 = m.M12, m11 = m.M22, m12 = m.OffsetY;

                double minX = double.MaxValue, minY = double.MaxValue;
                double maxX = double.MinValue, maxY = double.MinValue;
                var corners = new[] {
                    new Point2d(0, 0),
                    new Point2d(sourceMat.Width, 0),
                    new Point2d(0, sourceMat.Height),
                    new Point2d(sourceMat.Width, sourceMat.Height)
                };
                foreach (var corner in corners) {
                    double tx = m00 * corner.X + m01 * corner.Y + m02;
                    double ty = m10 * corner.X + m11 * corner.Y + m12;
                    minX = Math.Min(minX, tx);
                    maxX = Math.Max(maxX, tx);
                    minY = Math.Min(minY, ty);
                    maxY = Math.Max(maxY, ty);
                }
                // Subtract a tiny epsilon before ceiling: exact multiples of 90 degrees don't
                // give exactly 0/1 for cos/sin (e.g. Math.Cos(Math.PI / 2) is ~6.12e-17), so an
                // otherwise-integer bounding box like 2.0000000000000002 would otherwise ceiling
                // up to 3 and pad a spurious extra row/column onto the most common rotations.
                int outWidth = Math.Max(1, (int)Math.Ceiling(maxX - minX - 1e-9));
                int outHeight = Math.Max(1, (int)Math.Ceiling(maxY - minY - 1e-9));

                using var affine = new Mat(2, 3, MatType.CV_64F);
                affine.Set(0, 0, m00); affine.Set(0, 1, m01); affine.Set(0, 2, m02 - minX);
                affine.Set(1, 0, m10); affine.Set(1, 1, m11); affine.Set(1, 2, m12 - minY);
                Cv2.WarpAffine(sourceMat, result, affine, new OpenCvSharp.Size(outWidth, outHeight), InterpolationFlags.Linear, BorderTypes.Constant, Scalar.All(0));
            }

            SetResult(result);
        }

        private void SetResult(Mat result) {
            // EndInit() can run on an instance whose base constructor already allocated an
            // empty Mat - release it, and keep the GC pressure accounting in sync with the
            // (potentially large) transformed result.
            _mat?.Dispose();
            _mat = result;
            AddMemoryPressure();
        }
    }
}
