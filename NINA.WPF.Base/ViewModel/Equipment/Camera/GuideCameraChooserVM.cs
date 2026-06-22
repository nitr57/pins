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
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Image.Interfaces;
using NINA.Profile.Interfaces;

namespace NINA.WPF.Base.ViewModel.Equipment.Camera {

    /// <summary>
    /// A second, independent camera chooser used to select the dedicated guide camera for the
    /// integrated PHD2 guider. It is a distinct type from <see cref="CameraChooserVM"/> so DI can
    /// hold a separate singleton with its own selection/connection state — the guide camera must
    /// not collide with the imaging camera (they run in parallel).
    /// </summary>
    public class GuideCameraChooserVM : CameraChooserVM {

        public GuideCameraChooserVM(IProfileService profileService,
                                    ITelescopeMediator telescopeMediator,
                                    IExposureDataFactory exposureDataFactory,
                                    IImageDataFactory imageDataFactory,
                                    IEquipmentProviders<ICamera> equipmentProviders)
            : base(profileService, telescopeMediator, exposureDataFactory, imageDataFactory, equipmentProviders) {
        }
    }
}
