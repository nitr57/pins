#region "copyright"

/*
    Copyright © 2026 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

namespace NINA.Equipment.Interfaces.Mediator {

    /// <summary>
    /// The guide camera slot: a second camera next to the imaging camera, with its own settings
    /// (Profile.GuideCameraSettings). Guiders import this; ICameraMediator is always the imaging camera.
    /// </summary>
    public interface IGuideCameraMediator : ICameraMediator {

        /// <summary>
        /// Takes the capture block if nobody holds it, in one step, so two users of the guide camera (the guider, an API
        /// capture) can't both see it free and start an exposure. Release it with
        /// <see cref="ICameraMediator.ReleaseCaptureBlock(object)"/>.
        /// </summary>
        /// <returns>True when <paramref name="cameraConsumer"/> now holds the block, false when it was already held.</returns>
        bool TryRegisterCaptureBlock(object cameraConsumer);
    }
}
