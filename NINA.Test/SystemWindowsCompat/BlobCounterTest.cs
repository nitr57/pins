#region "copyright"

/*
    Copyright © 2025 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Accord.Imaging;
using NUnit.Framework;
using System.Drawing;
using System.Drawing.Imaging;

namespace NINA.Test.SystemWindowsCompat {

    [TestFixture]
    public class BlobCounterTest {

        private static Bitmap BinaryImageWithSquare(int size, Rectangle square) {
            var bitmap = new Bitmap(size, size, PixelFormat.Format8bppIndexed);
            for (int y = square.Top; y < square.Bottom; y++)
                for (int x = square.Left; x < square.Right; x++)
                    bitmap.SetPixel(x, y, Color.FromArgb(255, 255, 255));
            return bitmap;
        }

        [Test]
        public void SingleSquare_YieldsOneBlobWithCorrectBoundingBox() {
            using var image = BinaryImageWithSquare(20, new Rectangle(5, 5, 6, 6));
            using var counter = new BlobCounter();

            counter.ProcessImage(image);
            var blobs = counter.GetObjectsInformation();

            Assert.That(counter.ObjectsCount, Is.EqualTo(1));
            Assert.That(blobs[0].Rectangle, Is.EqualTo(new Rectangle(5, 5, 6, 6)));
        }

        // ProcessImage runs FindContours on the caller's image without copying it first, so it
        // must leave that image untouched and give the same blobs when run again.
        [Test]
        public void ProcessImage_LeavesInputUnchanged_AndIsRepeatable() {
            using var image = BinaryImageWithSquare(20, new Rectangle(5, 5, 6, 6));
            OpenCvSharp.Mat imageMat = image;
            using var before = imageMat.Clone();
            using var counter = new BlobCounter();

            counter.ProcessImage(image);
            using (var diff = new OpenCvSharp.Mat()) {
                OpenCvSharp.Cv2.Absdiff(imageMat, before, diff);
                Assert.That(OpenCvSharp.Cv2.CountNonZero(diff), Is.EqualTo(0));
            }

            counter.ProcessImage(image);
            Assert.That(counter.ObjectsCount, Is.EqualTo(1));
            Assert.That(counter.GetObjectsInformation()[0].Rectangle, Is.EqualTo(new Rectangle(5, 5, 6, 6)));
        }

        [Test]
        public void SingleSquare_CenterOfGravity_IsGeometricCenter() {
            using var image = BinaryImageWithSquare(20, new Rectangle(5, 5, 6, 6));
            using var counter = new BlobCounter();

            counter.ProcessImage(image);
            var blob = counter.GetObjectsInformation()[0];

            Assert.That(blob.CenterOfGravity.X, Is.EqualTo(7.5).Within(0.01));
            Assert.That(blob.CenterOfGravity.Y, Is.EqualTo(7.5).Within(0.01));
        }

        [Test]
        public void TwoSeparateSquares_YieldTwoBlobs() {
            using var image = new Bitmap(30, 30, PixelFormat.Format8bppIndexed);
            for (int y = 2; y < 8; y++)
                for (int x = 2; x < 8; x++)
                    image.SetPixel(x, y, Color.FromArgb(255, 255, 255));
            for (int y = 20; y < 26; y++)
                for (int x = 20; x < 26; x++)
                    image.SetPixel(x, y, Color.FromArgb(255, 255, 255));

            using var counter = new BlobCounter();
            counter.ProcessImage(image);

            Assert.That(counter.ObjectsCount, Is.EqualTo(2));
        }

        [Test]
        public void EmptyImage_YieldsNoBlobs() {
            using var image = new Bitmap(10, 10, PixelFormat.Format8bppIndexed);
            using var counter = new BlobCounter();

            counter.ProcessImage(image);

            Assert.That(counter.ObjectsCount, Is.EqualTo(0));
        }

        [Test]
        public void SizeFilter_DropsSmallBlobs() {
            using var image = new Bitmap(30, 30, PixelFormat.Format8bppIndexed);
            // Large 8x8 square
            for (int y = 2; y < 10; y++)
                for (int x = 2; x < 10; x++)
                    image.SetPixel(x, y, Color.FromArgb(255, 255, 255));
            // Small 4x4 square (below the filter threshold)
            for (int y = 20; y < 24; y++)
                for (int x = 20; x < 24; x++)
                    image.SetPixel(x, y, Color.FromArgb(255, 255, 255));

            using var counter = new BlobCounter {
                FilterBlobs = true,
                MinWidth = 6,
                MinHeight = 6
            };
            counter.ProcessImage(image);

            Assert.That(counter.ObjectsCount, Is.EqualTo(1));
            Assert.That(counter.GetObjectsInformation()[0].Rectangle.Width, Is.EqualTo(8));
        }

        [Test]
        public void GetBlobsEdgePoints_ReturnsContour() {
            using var image = BinaryImageWithSquare(20, new Rectangle(5, 5, 6, 6));
            using var counter = new BlobCounter();

            counter.ProcessImage(image);
            var edgePoints = counter.GetBlobsEdgePoints(counter.GetObjectsInformation()[0]);

            Assert.That(edgePoints, Is.Not.Empty);
            foreach (var point in edgePoints) {
                Assert.That(point.X, Is.InRange(5, 10));
                Assert.That(point.Y, Is.InRange(5, 10));
            }
        }

        [Test]
        public void Blob_Area_IsPixelArea() {
            using var image = BinaryImageWithSquare(20, new Rectangle(5, 5, 6, 6));
            using var counter = new BlobCounter();

            counter.ProcessImage(image);
            var blob = counter.GetObjectsInformation()[0];

            // A filled 6x6 square covers 36 pixels
            Assert.That(blob.Area, Is.EqualTo(36));
        }
    }
}
