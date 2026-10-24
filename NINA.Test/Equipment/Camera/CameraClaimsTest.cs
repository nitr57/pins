#region "copyright"

/*
    Copyright © 2026 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using ASCOM.Alpaca.Discovery;
using Moq;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Equipment.MyCamera.ToupTekAlike;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Utility;
using NINA.Image.Interfaces;
using NINA.Profile;
using NINA.Profile.Interfaces;
using NUnit.Framework;
using System;
using ProfileModel = NINA.Profile.Profile;

namespace NINA.Test.Equipment.Camera {

    /// <summary>
    /// The imaging and guide camera slots claim the physical camera they connect, so the other slot can't open it.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class CameraClaimsTest {
        private const string Imaging = "Camera";
        private const string Guide = "Guide camera";
        private const string Atr585 = "usb-0547-157c-4-4";
        private const string G3m678 = "usb-0547-14ae-4-3";

        private readonly object imagingSlot = new();
        private readonly object guideSlot = new();
        private ProfileModel profile = null!;
        private IProfileService profileService = null!;

        [SetUp]
        public void SetUp() {
            CameraClaims.ResetForTests();
            profile = new ProfileModel("Rig");
            var service = new Mock<IProfileService>();
            service.SetupGet(s => s.ActiveProfile).Returns(profile);
            profileService = service.Object;
        }

        [TearDown]
        public void TearDown() {
            CameraClaims.ResetForTests();
            profile.Dispose();
        }

        private ToupTekAlikeCamera ToupTek(string brand, string sdkId) {
            var sdk = new Mock<IToupTekAlikeCameraSDK>();
            sdk.SetupGet(x => x.Category).Returns(brand);
            var info = new ToupTekAlikeDeviceInfo {
                displayname = "ATR585M",
                id = sdkId,
                model = new ToupTekAlikeModel { xpixsz = 2.9f, ypixsz = 2.9f }
            };
            return new ToupTekAlikeCamera(info, sdk.Object, profileService, Mock.Of<IExposureDataFactory>());
        }

        private AlpacaDirectCamera AlpacaDirect(string id, string ipAddress, int port = 11111, int deviceNumber = 0) {
            var settings = new PluginOptionsAccessor(profileService, Guid.Parse(id));
            settings.SetValueString("IpAddress", ipAddress);
            settings.SetValueInt32("Port", port);
            settings.SetValueInt32("DeviceNumber", deviceNumber);
            return new AlpacaDirectCamera(profileService, Mock.Of<IExposureDataFactory>(), id);
        }

        private AscomCamera DiscoveredAlpaca(string uniqueId, string ipAddress, int port = 11111, int deviceNumber = 0) {
            var meta = new AscomDevice {
                UniqueId = uniqueId,
                AscomDeviceName = "ZWO ASI Air",
                HostName = ipAddress,
                IpAddress = ipAddress,
                IpPort = port,
                AlpacaDeviceNumber = deviceNumber
            };
            return new AscomCamera(meta, profileService, Mock.Of<IExposureDataFactory>());
        }

        private static ICamera Camera(string category, string id) {
            return Mock.Of<ICamera>(c => c.Category == category && c.Id == id && c.DisplayName == $"{category} {id}");
        }

        [Test]
        public void TheSameToupTekCamera_UnderAnotherBrand_IsRefused() {
            var asToupTek = ToupTek("ToupTek", Atr585);
            var asRisingCam = ToupTek("RisingCam", Atr585);
            Assert.That(asToupTek.Id, Is.Not.EqualTo(asRisingCam.Id), "the device Ids differ, the camera doesn't");

            Assert.That(CameraClaims.TryClaim(imagingSlot, Imaging, asToupTek, out _), Is.True);
            Assert.That(CameraClaims.TryClaim(guideSlot, Guide, asRisingCam, out var refusal), Is.False);
            Assert.That(refusal, Does.Contain(asRisingCam.DisplayName).And.Contain(Imaging));
        }

        [Test]
        public void TwoDifferentToupTekCameras_AreAllowed() {
            Assert.That(CameraClaims.TryClaim(imagingSlot, Imaging, ToupTek("ToupTek", Atr585), out _), Is.True);
            Assert.That(CameraClaims.TryClaim(guideSlot, Guide, ToupTek("ToupTek", G3m678), out _), Is.True);
        }

        [Test]
        public void ARefusal_GoesBothWays() {
            Assert.That(CameraClaims.TryClaim(guideSlot, Guide, ToupTek("ToupTek", G3m678), out _), Is.True);

            Assert.That(CameraClaims.TryClaim(imagingSlot, Imaging, ToupTek("Omegon", G3m678), out var refusal), Is.False);
            Assert.That(refusal, Does.Contain(Guide));
        }

        [Test]
        public void AReleasedCamera_CanBeClaimedByTheOtherSlot() {
            CameraClaims.TryClaim(imagingSlot, Imaging, ToupTek("ToupTek", Atr585), out _);

            CameraClaims.Release(imagingSlot);

            Assert.That(CameraClaims.TryClaim(guideSlot, Guide, ToupTek("ToupTek", Atr585), out _), Is.True);
        }

        [Test]
        public void ClaimingAnotherCamera_ReleasesTheSlotsPreviousOne() {
            CameraClaims.TryClaim(imagingSlot, Imaging, ToupTek("ToupTek", Atr585), out _);

            CameraClaims.TryClaim(imagingSlot, Imaging, ToupTek("ToupTek", G3m678), out _);

            Assert.That(CameraClaims.TryClaim(guideSlot, Guide, ToupTek("ToupTek", Atr585), out _), Is.True);
        }

        [Test]
        public void AFailedClaim_LeavesTheOtherSlotsClaimAlone() {
            CameraClaims.TryClaim(imagingSlot, Imaging, ToupTek("ToupTek", Atr585), out _);
            CameraClaims.TryClaim(guideSlot, Guide, ToupTek("ToupTek", Atr585), out _);

            Assert.That(CameraClaims.TryClaim(guideSlot, Guide, ToupTek("Omegon", Atr585), out _), Is.False);
        }

        [Test]
        public void TheSameQhyCamera_IsRefused_AnotherOneIsAllowed() {
            Assert.That(CameraClaims.TryClaim(imagingSlot, Imaging, Camera("QHYCCD", "QHY268M-1234"), out _), Is.True);

            Assert.That(CameraClaims.TryClaim(guideSlot, Guide, Camera("QHYCCD", "QHY268M-1234"), out var refusal), Is.False);
            Assert.That(refusal, Does.Contain(Imaging));

            Assert.That(CameraClaims.TryClaim(guideSlot, Guide, Camera("QHYCCD", "QHY5III462C-5678"), out _), Is.True);
        }

        [Test]
        public void TheSameIndiDevice_IsRefused_AnotherOneIsAllowed() {
            Assert.That(CameraClaims.TryClaim(imagingSlot, Imaging, Camera("INDI", "CCD Simulator"), out _), Is.True);

            Assert.That(CameraClaims.TryClaim(guideSlot, Guide, Camera("INDI", "CCD Simulator"), out _), Is.False);
            Assert.That(CameraClaims.TryClaim(guideSlot, Guide, Camera("INDI", "Guide Simulator"), out _), Is.True);
        }

        [Test]
        public void TheSimulator_CanBeInBothSlots() {
            var imagingSimulator = new SimpleSimulatorCamera(profileService, Mock.Of<IImageDataFactory>(), Mock.Of<IExposureDataFactory>());
            var guideSimulator = new SimpleSimulatorCamera(profileService, Mock.Of<IImageDataFactory>(), Mock.Of<IExposureDataFactory>());

            Assert.That(CameraClaims.TryClaim(imagingSlot, Imaging, imagingSimulator, out _), Is.True);
            Assert.That(CameraClaims.TryClaim(guideSlot, Guide, guideSimulator, out _), Is.True);
        }

        [Test]
        public void TwoAlpacaCamerasByStaticIp_AtDifferentAddresses_AreAllowed() {
            var imaging = AlpacaDirect(AlpacaInteraction.AlpacaDirectCameraId, "192.168.1.50");
            var guide = AlpacaDirect(AlpacaInteraction.GuideAlpacaDirectCameraId, "192.168.1.51");

            Assert.That(CameraClaims.TryClaim(imagingSlot, Imaging, imaging, out _), Is.True);
            Assert.That(CameraClaims.TryClaim(guideSlot, Guide, guide, out _), Is.True);
        }

        [Test]
        public void TheSameAddress_InBothStaticIpEntries_IsRefused() {
            var imaging = AlpacaDirect(AlpacaInteraction.AlpacaDirectCameraId, "192.168.1.50");
            var guide = AlpacaDirect(AlpacaInteraction.GuideAlpacaDirectCameraId, " 192.168.1.50 ");
            Assert.That(imaging.Id, Is.Not.EqualTo(guide.Id), "the device Ids differ, the camera doesn't");

            Assert.That(CameraClaims.TryClaim(imagingSlot, Imaging, imaging, out _), Is.True);
            Assert.That(CameraClaims.TryClaim(guideSlot, Guide, guide, out var refusal), Is.False);
            Assert.That(refusal, Does.Contain(Imaging));
        }

        [Test]
        public void TwoDevicesOfOneAlpacaServer_AreAllowed() {
            var imaging = AlpacaDirect(AlpacaInteraction.AlpacaDirectCameraId, "192.168.1.50", deviceNumber: 0);
            var guide = AlpacaDirect(AlpacaInteraction.GuideAlpacaDirectCameraId, "192.168.1.50", deviceNumber: 1);

            Assert.That(CameraClaims.TryClaim(imagingSlot, Imaging, imaging, out _), Is.True);
            Assert.That(CameraClaims.TryClaim(guideSlot, Guide, guide, out _), Is.True);
        }

        [Test]
        public void ADiscoveredAlpacaCamera_AndTheSameOneByStaticIp_AreRefused() {
            Assert.That(CameraClaims.TryClaim(imagingSlot, Imaging, DiscoveredAlpaca("unique-1", "192.168.1.50"), out _), Is.True);

            var guide = AlpacaDirect(AlpacaInteraction.GuideAlpacaDirectCameraId, "192.168.1.50");
            Assert.That(CameraClaims.TryClaim(guideSlot, Guide, guide, out _), Is.False);
        }

        [Test]
        public void TwoDiscoveredAlpacaCameras_WithOneUniqueId_AreAllowedAtDifferentAddresses() {
            Assert.That(CameraClaims.TryClaim(imagingSlot, Imaging, DiscoveredAlpaca("same-id", "192.168.1.50"), out _), Is.True);
            Assert.That(CameraClaims.TryClaim(guideSlot, Guide, DiscoveredAlpaca("same-id", "192.168.1.51"), out _), Is.True);
        }

        [Test]
        public void TheCameraInsideTheSettingsDecorator_IsWhatCounts() {
            var decorated = new PersistSettingsCameraDecorator(profileService, ToupTek("ToupTek", Atr585));
            CameraClaims.TryClaim(imagingSlot, Imaging, decorated, out _);

            Assert.That(CameraClaims.TryClaim(guideSlot, Guide, ToupTek("RisingCam", Atr585), out _), Is.False);
        }
    }
}
