#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Equipment.Model;
using NINA.Profile.Interfaces;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Equipment.Equipment.MyGuider.PHD2 {

    /// <summary>
    /// <see cref="IGuideCameraSource"/> backed by a dedicated <see cref="ICamera"/> instance,
    /// kept separate from the imaging camera (integration plan §6) so imaging and guiding can run
    /// in parallel. On <see cref="Connect"/> it picks the camera saved as
    /// <c>GuiderSettings.IntegratedGuideCameraId</c> from a dedicated camera chooser (distinct from
    /// the imaging chooser) and owns the exposure → download → raw-pixel extraction.
    /// </summary>
    public class CameraGuideCameraSource : IGuideCameraSource {
        private readonly IProfileService profileService;
        private readonly IDeviceChooserVM cameraChooser;

        public CameraGuideCameraSource(IProfileService profileService, IDeviceChooserVM cameraChooser) {
            this.profileService = profileService;
            this.cameraChooser = cameraChooser;
        }

        /// <summary>The dedicated guide camera, resolved from the chooser on <see cref="Connect"/>.</summary>
        public ICamera Camera { get; set; }

        public bool Connected => Camera?.Connected ?? false;

        public string Name => Camera?.DisplayName ?? string.Empty;

        public double PixelSizeMicrons {
            get {
                if (Camera == null) { return 0.0; }
                // Effective pixel size grows with binning.
                var bin = Camera.BinX > 0 ? Camera.BinX : (short)1;
                return Camera.PixelSizeX * bin;
            }
        }

        public async Task<bool> Connect(CancellationToken token) {
            Camera = await ResolveCamera(token);
            if (Camera == null) { return false; }
            if (!Camera.Connected) {
                if (!await Camera.Connect(token)) {
                    return false;
                }
            }
            return Camera.Connected;
        }

        private async Task<ICamera> ResolveCamera(CancellationToken token) {
            // Pre-bound camera (e.g. for tests) takes precedence.
            if (Camera != null) { return Camera; }
            if (cameraChooser == null) { return null; }

            var id = profileService.ActiveProfile.GuiderSettings.IntegratedGuideCameraId;
            if (string.IsNullOrEmpty(id)) { return null; }

            await cameraChooser.GetEquipment();
            var device = cameraChooser.Devices.FirstOrDefault(d => d.Id == id) as ICamera;
            if (device != null) {
                cameraChooser.SelectedDevice = device;
            }
            return device;
        }

        public void Disconnect() {
            Camera?.Disconnect();
            // Drop the binding so a later Connect re-resolves the current selection.
            Camera = null;
        }

        public async Task<GuideFrame> CaptureFrame(double exposureSeconds, CancellationToken token) {
            if (Camera == null || !Camera.Connected) {
                throw new InvalidOperationException("Guide camera is not connected");
            }

            var sequence = new CaptureSequence {
                ExposureTime = exposureSeconds,
                ImageType = CaptureSequence.ImageTypes.SNAPSHOT,
                TotalExposureCount = 1,
                Gain = -1,
                Offset = -1
            };

            Camera.StartExposure(sequence);
            await Camera.WaitUntilExposureIsReady(token);
            var exposureData = await Camera.DownloadExposure(token);
            token.ThrowIfCancellationRequested();

            var imageData = await exposureData.ToImageData(default, token);
            return new GuideFrame(
                imageData.Data.FlatArray,
                imageData.Properties.Width,
                imageData.Properties.Height,
                PixelSizeMicrons,
                imageData);
        }
    }
}
