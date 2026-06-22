#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Core.Interfaces;
using System;

namespace NINA.Equipment.Equipment.MyGuider.PHD2 {

    /// <summary>
    /// <see cref="IGuideStep"/> produced by <see cref="IntegratedGuider"/> from the native
    /// libphd2core guide step. Distances are in guide-camera pixels; durations in seconds.
    /// </summary>
    public class IntegratedGuideStep : IGuideStep {
        public string Event => "GuideStep";
        public string TimeStamp => DateTime.Now.ToString("o");
        public string Host => Environment.MachineName;
        public int Inst => 1;

        public double Frame { get; set; }
        public double Time { get; set; }

        public double RADistanceRaw { get; set; }
        public double DECDistanceRaw { get; set; }
        public double RADuration { get; set; }
        public double DECDuration { get; set; }

        public IGuideStep Clone() {
            return (IGuideStep)MemberwiseClone();
        }
    }
}
