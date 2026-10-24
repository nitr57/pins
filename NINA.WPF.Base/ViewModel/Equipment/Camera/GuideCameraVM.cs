#region "copyright"

/*
    Copyright © 2026 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile;
using NINA.WPF.Base.Interfaces.Mediator;

namespace NINA.WPF.Base.ViewModel.Equipment.Camera {

    /// <summary>
    /// The guide camera slot: the imaging camera's view model run a second time with the guide camera's settings.
    /// Keep logic out of here; CameraVM tests need STA, which doesn't run on Linux.
    /// </summary>
    public class GuideCameraVM : CameraVM {

        // No filter wheel: guide frames don't take its filter (CameraVM.Capture checks for null).
        public GuideCameraVM(GuideCameraProfileService profileService,
                             IGuideCameraMediator guideCameraMediator,
                             IApplicationStatusMediator applicationStatusMediator,
                             GuideCameraChooserVM guideCameraChooserVM)
            : base(profileService, guideCameraMediator, null, applicationStatusMediator, guideCameraChooserVM) {
            Title = "Guide camera";
        }

        // a guide camera takes a frame every few seconds all night
        protected override bool LogFramesAtDebug => true;
    }
}
