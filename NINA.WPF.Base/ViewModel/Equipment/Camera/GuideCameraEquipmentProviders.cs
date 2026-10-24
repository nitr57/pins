#region "copyright"

/*
    Copyright © 2026 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Core.Utility;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Equipment.SDK.CameraSDKs.PlayerOneSDK;
using NINA.Equipment.SDK.CameraSDKs.SVBonySDK;
using NINA.Image.Interfaces;
using NINA.Profile;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace NINA.WPF.Base.ViewModel.Equipment.Camera {

    /// <summary>
    /// The camera providers for the guide camera's list. Providers are composed once with the application's
    /// profile service, so their cameras would read and write the imaging camera's settings. PlayerOne and
    /// SVBony are built again with the guide camera's profile service; other providers can't be, and are left out.
    /// </summary>
    public class GuideCameraEquipmentProviders : IEquipmentProviders<ICamera> {
        private readonly IEquipmentProviders<ICamera> cameraProviders;
        private readonly GuideCameraProfileService profileService;
        private readonly IExposureDataFactory exposureDataFactory;

        public GuideCameraEquipmentProviders(IEquipmentProviders<ICamera> cameraProviders,
                                             GuideCameraProfileService profileService,
                                             IExposureDataFactory exposureDataFactory) {
            this.cameraProviders = cameraProviders;
            this.profileService = profileService;
            this.exposureDataFactory = exposureDataFactory;
        }

        public bool Initialized {
            get => cameraProviders.Initialized;
            set => cameraProviders.Initialized = value;
        }

        public Type GetInterfaceType() => typeof(ICamera);

        public void AddProvider(IEquipmentProvider deviceProvider) {
            throw new NotSupportedException("Camera providers are added to the imaging camera's provider list.");
        }

        public async Task<IList<IEquipmentProvider<ICamera>>> GetProviders() {
            var providers = new List<IEquipmentProvider<ICamera>>();
            foreach (var provider in await cameraProviders.GetProviders()) {
                switch (provider) {
                    case PlayerOneProvider:
                        providers.Add(new PlayerOneProvider(profileService, exposureDataFactory));
                        break;
                    case SVBonyProvider:
                        providers.Add(new SVBonyProvider(profileService, exposureDataFactory));
                        break;
                    default:
                        Logger.Info($"{provider.Name} cameras are not offered as guide cameras: their provider can't use separate guide camera settings");
                        break;
                }
            }
            return providers;
        }
    }
}
