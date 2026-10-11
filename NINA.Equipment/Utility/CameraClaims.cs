#region "copyright"

/*
    Copyright © 2026 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Interfaces;
using System.Collections.Generic;
using System.Linq;

namespace NINA.Equipment.Utility {

    /// <summary>
    /// Which physical camera each camera slot (imaging, guide) has connected, so the other slot can't open it too.
    /// A camera is identified by what its SDK knows it by, not by its device Id: one ToupTek camera is listed under
    /// several brands (ToupTek_usb-0547-157c-4-4, Omegon_usb-0547-157c-4-4, ...) and two identical ASI cameras
    /// without an alias share an Id.
    /// </summary>
    public static class CameraClaims {
        private static readonly Dictionary<object, Claim> claims = [];

        private sealed record Claim(string Key, string OwnerName);

        /// <summary>
        /// Claims <paramref name="camera"/> for <paramref name="owner"/>, replacing what it held before.
        /// Returns false, with a message for the user, when another owner has the same camera.
        /// </summary>
        public static bool TryClaim(object owner, string ownerName, ICamera camera, out string refusal) {
            refusal = null;
            var key = KeyOf(camera);
            lock (claims) {
                claims.Remove(owner);
                if (key == null) {
                    return true;
                }
                var holder = claims.FirstOrDefault(c => c.Value.Key == key).Value;
                if (holder != null) {
                    refusal = $"{camera.DisplayName} is already connected as {holder.OwnerName}. Disconnect it there first.";
                    return false;
                }
                claims[owner] = new Claim(key, ownerName);
                return true;
            }
        }

        public static void Release(object owner) {
            lock (claims) {
                claims.Remove(owner);
            }
        }

        /// <summary>The physical camera behind <paramref name="camera"/>; null for a camera any number of slots may use.</summary>
        internal static string KeyOf(ICamera camera) {
            while (camera is PersistSettingsCameraDecorator decorator) {
                camera = decorator.Camera;
            }
            switch (camera) {
                case null:
                case SimpleSimulatorCamera:
                    return null;

                case ToupTekAlikeCamera toupTek:
                    // Id is "<brand>_<SDK id>"; every brand's SDK reports the same id for one camera.
                    var prefix = toupTek.Category + "_";
                    var sdkId = toupTek.Id.StartsWith(prefix) ? toupTek.Id[prefix.Length..] : toupTek.Id;
                    return "ToupTekAlike:" + sdkId;

                case ASICamera asi:
                    return "ZWOptical:" + asi.CameraId;
            }
            return camera.Category + ":" + camera.Id;
        }

        internal static void ResetForTests() {
            lock (claims) {
                claims.Clear();
            }
        }
    }
}
