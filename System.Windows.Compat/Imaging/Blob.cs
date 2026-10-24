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
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using CVPoint = OpenCvSharp.Point;
using AccordPoint = Accord.IntPoint;

namespace Accord.Imaging {
    /// <summary>
    /// Blob object representing a connected region in an image
    /// Uses OpenCV for blob detection
    /// </summary>
    public class Blob {
        /// <summary>
        /// Rectangle that bounds the blob
        /// </summary>
        public Rectangle Rectangle { get; set; }

        private int? _area;

        /// <summary>
        /// Area of the blob in pixels. Computed lazily on first access: the pixel-accurate
        /// count needs a mask fill per blob, which is far too expensive to pay eagerly for
        /// every contour of every star-detection frame when most callers never read it.
        /// </summary>
        public int Area {
            get {
                if (_area == null) {
                    ComputeAreaAndFullness();
                }
                return _area ?? 0;
            }
            set => _area = value;
        }

        /// <summary>
        /// Center of gravity (centroid) of the blob
        /// </summary>
        public Point2d CenterOfGravity { get; set; }

        private double? _fullness;

        /// <summary>
        /// Fullness of the blob (ratio of area to bounding rectangle area). Lazy, see Area.
        /// </summary>
        public double Fullness {
            get {
                if (_fullness == null) {
                    ComputeAreaAndFullness();
                }
                return _fullness ?? 0;
            }
            set => _fullness = value;
        }

        /// <summary>
        /// The contour points that define this blob
        /// </summary>
        internal CVPoint[] EdgePoints { get; set; }

        /// <summary>
        /// Unique identifier for this blob
        /// </summary>
        public int ID { get; set; }

        internal Blob(int id, CVPoint[] contour) {
            ID = id;
            EdgePoints = contour;

            if (contour != null && contour.Length > 0) {
                var rect = Cv2.BoundingRect(contour);
                Rectangle = new Rectangle(rect.X, rect.Y, rect.Width, rect.Height);

                // Calculate center of gravity
                var moments = Cv2.Moments(contour);
                if (moments.M00 != 0) {
                    CenterOfGravity = new Point2d(moments.M10 / moments.M00, moments.M01 / moments.M00);
                } else {
                    CenterOfGravity = new Point2d(Rectangle.X + Rectangle.Width / 2.0, Rectangle.Y + Rectangle.Height / 2.0);
                }
            }
        }

        private void ComputeAreaAndFullness() {
            if (EdgePoints == null || EdgePoints.Length == 0 || Rectangle.Width <= 0 || Rectangle.Height <= 0) {
                _area = 0;
                _fullness = 0;
                return;
            }

            // Cv2.ContourArea uses the shoelace formula on the corner polygon (undercounts by a border of
            // half a pixel on each side), so fill the contour into a bounding-rect mask and count pixels to
            // match Accord's actual pixel-count semantics.
            using (var mask = new Mat(Rectangle.Height, Rectangle.Width, MatType.CV_8UC1, Scalar.Black)) {
                var shifted = EdgePoints.Select(p => new CVPoint(p.X - Rectangle.X, p.Y - Rectangle.Y)).ToArray();
                Cv2.FillPoly(mask, new[] { shifted }, Scalar.White);
                _area = Cv2.CountNonZero(mask);
            }

            double rectArea = (double)Rectangle.Width * Rectangle.Height;
            _fullness = _area.Value / rectArea;
        }
    }

    /// <summary>
    /// Blob counter - counts and extracts stand alone objects in images
    /// Uses OpenCV's findContours for blob detection
    /// </summary>
    public class BlobCounter : System.IDisposable {
        private List<Blob> blobs = new List<Blob>();

        /// <summary>
        /// Minimum width of a blob to be included
        /// </summary>
        public int MinWidth { get; set; } = 1;

        /// <summary>
        /// Minimum height of a blob to be included
        /// </summary>
        public int MinHeight { get; set; } = 1;

        /// <summary>
        /// Maximum width of a blob to be included
        /// </summary>
        public int MaxWidth { get; set; } = int.MaxValue;

        /// <summary>
        /// Maximum height of a blob to be included
        /// </summary>
        public int MaxHeight { get; set; } = int.MaxValue;

        /// <summary>
        /// Filter blobs by size
        /// </summary>
        public bool FilterBlobs { get; set; } = false;

        /// <summary>
        /// Process image searching for blobs
        /// </summary>
        /// <param name="image">Binary image to process</param>
        public void ProcessImage(Bitmap image) {
            blobs.Clear();

            // Convert Bitmap to Mat (Bitmap has implicit conversion to Mat in our implementation)
            Mat mat = image;

            // Find contours using OpenCV. The input is read-only for FindContours (it works on a
            // bordered copy internally since OpenCV 3.2), so the image needs no copy of its own -
            // keeping one here pinned a full frame per detection until the counter was disposed,
            // which NINA's star detection never does.
            CVPoint[][] contours;
            HierarchyIndex[] hierarchy;
            Cv2.FindContours(mat, out contours, out hierarchy, RetrievalModes.External, ContourApproximationModes.ApproxSimple);

            // Convert contours to blobs
            int id = 0;
            foreach (var contour in contours) {
                if (contour.Length < 3) continue; // Skip contours with too few points

                var blob = new Blob(id++, contour);

                // Apply size filters if enabled
                if (FilterBlobs) {
                    if (blob.Rectangle.Width < MinWidth || blob.Rectangle.Width > MaxWidth ||
                        blob.Rectangle.Height < MinHeight || blob.Rectangle.Height > MaxHeight) {
                        continue;
                    }
                }

                blobs.Add(blob);
            }
        }

        /// <summary>
        /// Get information about detected blobs
        /// </summary>
        /// <returns>Array of detected blobs</returns>
        public Blob[] GetObjectsInformation() {
            return blobs.ToArray();
        }

        /// <summary>
        /// Get edge points of a specific blob
        /// </summary>
        /// <param name="blob">Blob to get edge points for</param>
        /// <returns>List of edge points as Accord.IntPoint</returns>
        public List<AccordPoint> GetBlobsEdgePoints(Blob blob) {
            if (blob.EdgePoints == null || blob.EdgePoints.Length == 0) {
                return new List<AccordPoint>();
            }

            // Convert OpenCV Points to Accord IntPoints
            return blob.EdgePoints.Select(p => new AccordPoint(p.X, p.Y)).ToList();
        }

        /// <summary>
        /// Get number of detected objects
        /// </summary>
        public int ObjectsCount => blobs.Count;

        /// <summary>
        /// Nothing to release any more - the counter holds only managed contour data. Kept so
        /// existing `using` call sites keep compiling.
        /// </summary>
        public void Dispose() {
        }
    }
}
