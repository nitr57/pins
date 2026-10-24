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
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Utility;
using NINA.Image.Interfaces;
using NINA.Profile;
using System.Threading.Tasks;

namespace NINA.WPF.Base.ViewModel.Equipment.Camera {

    /// <summary>
    /// The guide camera's device list. Every camera in it is built with the guide camera's profile service,
    /// so it reads and writes Profile.GuideCameraSettings instead of the imaging camera's settings.
    /// </summary>
    public class GuideCameraChooserVM : CameraChooserVM {

        public GuideCameraChooserVM(GuideCameraProfileService profileService,
                                    ITelescopeMediator telescopeMediator,
                                    IExposureDataFactory exposureDataFactory,
                                    IImageDataFactory imageDataFactory,
                                    GuideCameraEquipmentProviders equipmentProviders)
            : base(profileService, telescopeMediator, exposureDataFactory, imageDataFactory, equipmentProviders) {
        }

        protected override string IndiCategory => "GuideCamera";

        // DSLRs don't guide.
        protected override bool IncludeGPhotoCameras => false;

        // Its own Alpaca Direct entry, so the guide camera's address doesn't overwrite the imaging camera's.
        protected override string AlpacaDirectCameraId => AlpacaInteraction.GuideAlpacaDirectCameraId;

        public override async Task GetEquipment() {
            Logger.Info("Scanning for guide cameras");
            await base.GetEquipment();
        }
    }
}
