#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Image.Interfaces;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Equipment.Equipment.MyGuider.PHD2 {

    /// <summary>
    /// A single mono guide-camera exposure: row-major 16-bit pixels plus geometry, and the
    /// rendered <see cref="IImageData"/> (kept so the guide image can be served to the UI).
    /// </summary>
    public sealed class GuideFrame {
        public GuideFrame(ushort[] pixels, int width, int height, double pixelSizeMicrons, IImageData imageData = null) {
            Pixels = pixels;
            Width = width;
            Height = height;
            PixelSizeMicrons = pixelSizeMicrons;
            ImageData = imageData;
        }

        public ushort[] Pixels { get; }
        public int Width { get; }
        public int Height { get; }

        /// <summary>Physical pixel size (after binning) in microns; used for the guide pixel scale.</summary>
        public double PixelSizeMicrons { get; }

        /// <summary>The downloaded frame as image data, for rendering/serving the guide image.</summary>
        public IImageData ImageData { get; }
    }

    /// <summary>
    /// Frame source for <see cref="IntegratedGuider"/>. Abstracts the guide camera so the
    /// guiding engine integration is independent of how the camera is selected and owned.
    /// Per the integration plan (§6) the concrete implementation binds a dedicated
    /// <c>ICamera</c> instance kept off the imaging mediator.
    /// </summary>
    public interface IGuideCameraSource {
        bool Connected { get; }
        string Name { get; }

        /// <summary>Physical pixel size (after binning) in microns; used for the guide pixel scale.</summary>
        double PixelSizeMicrons { get; }

        Task<bool> Connect(CancellationToken token);
        void Disconnect();

        /// <summary>Capture and download a single mono frame.</summary>
        Task<GuideFrame> CaptureFrame(double exposureSeconds, CancellationToken token);
    }
}
