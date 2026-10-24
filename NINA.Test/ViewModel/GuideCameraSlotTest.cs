#region "copyright"

/*
    Copyright © 2026 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Moq;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Equipment.SDK.CameraSDKs.PlayerOneSDK;
using NINA.Equipment.SDK.CameraSDKs.SVBonySDK;
using NINA.Equipment.Utility;
using NINA.Image.Interfaces;
using NINA.Profile;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.ViewModel.Equipment.Camera;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using ProfileModel = NINA.Profile.Profile;

namespace NINA.Test.ViewModel {

    /// <summary>
    /// The parts of the guide camera slot that decide which cameras its list offers. GuideCameraVM itself adds
    /// nothing to CameraVM but its title, and CameraVM tests need STA, which doesn't run on Linux.
    /// </summary>
    [TestFixture]
    public class GuideCameraSlotTest {
        private ProfileModel profile = null!;
        private IProfileService imaging = null!;
        private GuideCameraProfileService guide = null!;
        private IExposureDataFactory exposureDataFactory = null!;

        [SetUp]
        public void SetUp() {
            profile = new ProfileModel("Rig");
            var profileService = new Mock<IProfileService>();
            profileService.SetupGet(s => s.ActiveProfile).Returns(profile);
            imaging = profileService.Object;
            guide = new GuideCameraProfileService(imaging);
            exposureDataFactory = Mock.Of<IExposureDataFactory>();
        }

        [TearDown]
        public void TearDown() {
            profile.Dispose();
        }

        private Task<IList<IEquipmentProvider<ICamera>>> GuideProvidersOf(params IEquipmentProvider<ICamera>[] providers) {
            var cameraProviders = new Mock<IEquipmentProviders<ICamera>>();
            cameraProviders.Setup(p => p.GetProviders()).ReturnsAsync(providers.ToList());
            return new GuideCameraEquipmentProviders(cameraProviders.Object, guide, exposureDataFactory).GetProviders();
        }

        private static object? ProfileServiceOf(object provider) {
            return provider.GetType().GetField("profileService", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(provider);
        }

        [Test]
        public async Task PlayerOneAndSVBony_AreRebuiltWithTheGuideCamerasSettings() {
            var providers = await GuideProvidersOf(
                new PlayerOneProvider(imaging, exposureDataFactory, Mock.Of<IPlayerOnePInvokeProxy>()),
                new SVBonyProvider(imaging, exposureDataFactory, Mock.Of<ISVBonyPInvokeProxy>()));

            Assert.That(providers.Select(p => p.GetType()), Is.EqualTo(new[] { typeof(PlayerOneProvider), typeof(SVBonyProvider) }));
            Assert.That(providers.Select(ProfileServiceOf), Is.All.SameAs(guide));
        }

        [Test]
        public async Task OtherProviders_AreLeftOut() {
            var thirdParty = Mock.Of<IEquipmentProvider<ICamera>>(p => p.Name == "Third Party");

            var providers = await GuideProvidersOf(thirdParty, new PlayerOneProvider(imaging, exposureDataFactory, Mock.Of<IPlayerOnePInvokeProxy>()));

            Assert.That(providers.Select(p => p.GetType()), Is.EqualTo(new[] { typeof(PlayerOneProvider) }));
        }

        private static string? AlpacaDirectIdOf(CameraChooserVM chooser) {
            return (string?)typeof(CameraChooserVM)
                .GetProperty("AlpacaDirectCameraId", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(chooser);
        }

        [Test]
        public void EachSlot_OffersItsOwnAlpacaDirectEntry() {
            var cameraProviders = Mock.Of<IEquipmentProviders<ICamera>>();
            var imagingChooser = new CameraChooserVM(imaging, Mock.Of<ITelescopeMediator>(), exposureDataFactory, Mock.Of<IImageDataFactory>(), cameraProviders);
            var guideChooser = new GuideCameraChooserVM(
                guide,
                Mock.Of<ITelescopeMediator>(),
                exposureDataFactory,
                Mock.Of<IImageDataFactory>(),
                new GuideCameraEquipmentProviders(cameraProviders, guide, exposureDataFactory));

            Assert.That(AlpacaDirectIdOf(imagingChooser), Is.EqualTo(AlpacaInteraction.AlpacaDirectCameraId));
            Assert.That(AlpacaDirectIdOf(guideChooser), Is.EqualTo(AlpacaInteraction.GuideAlpacaDirectCameraId));
        }

        [Test]
        public void TheGuideCamerasStaticIp_IsKeptApartFromTheImagingCameras() {
            var imagingEntry = new AlpacaDirectCamera(imaging, exposureDataFactory);
            var guideEntry = new AlpacaDirectCamera(guide, exposureDataFactory, AlpacaInteraction.GuideAlpacaDirectCameraId);

            // Written the way Touch-N-Stars' alpaca-direct endpoint does: with the imaging profile service.
            new PluginOptionsAccessor(imaging, Guid.Parse(AlpacaInteraction.AlpacaDirectCameraId)).SetValueString("IpAddress", "192.168.1.50");
            new PluginOptionsAccessor(imaging, Guid.Parse(AlpacaInteraction.GuideAlpacaDirectCameraId)).SetValueString("IpAddress", "192.168.1.51");

            Assert.That(imagingEntry.Id, Is.EqualTo(AlpacaInteraction.AlpacaDirectCameraId));
            Assert.That(guideEntry.Id, Is.EqualTo(AlpacaInteraction.GuideAlpacaDirectCameraId));
            Assert.That(imagingEntry.Endpoint, Does.StartWith("192.168.1.50:"));
            Assert.That(guideEntry.Endpoint, Does.StartWith("192.168.1.51:"));
        }
    }
}
