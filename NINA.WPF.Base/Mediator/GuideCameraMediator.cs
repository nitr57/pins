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

namespace NINA.WPF.Base.Mediator {

    /// <summary>
    /// The guide camera's mediator. A separate instance from the imaging camera's, since a mediator takes one handler.
    /// </summary>
    public class GuideCameraMediator : CameraMediator, IGuideCameraMediator {
    }
}
