#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System.Runtime.InteropServices;

namespace NINA.Equipment.SDK.CameraSDKs.SVBonySDK {

    public class SVBonyControl {
        private SVB_CONTROL_CAPS capabilities;

        public SVBonyControl(int index, SVB_CONTROL_CAPS capabilities) {
            this.Index = index;
            this.capabilities = capabilities;
        }

        public int Index { get; }
        public int Min { get => (int)capabilities.MinValue.Value; }
        public int Max { get => (int)capabilities.MaxValue.Value; }

        public override string ToString() {
            return $"Index: {Index}, Name: {capabilities.Name}, Description: {capabilities.Description}, Min: {Min}, Max: {Max}, Default: {(int)capabilities.DefaultValue.Value}, Auto: {capabilities.IsAutoSupported}, Writable: {capabilities.IsWritable}, ControlType: {capabilities.ControlType}";
        }
    }
}