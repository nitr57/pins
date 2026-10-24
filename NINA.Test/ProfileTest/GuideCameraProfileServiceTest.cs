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
using NINA.Profile;
using NINA.Profile.Interfaces;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ProfileModel = NINA.Profile.Profile;

namespace NINA.Test.ProfileTest {

    /// <summary>
    /// The guide camera stack runs with a GuideCameraProfileService, so its CameraSettings must be the profile's
    /// GuideCameraSettings and nothing else may differ from the real profile service.
    /// </summary>
    [TestFixture]
    public class GuideCameraProfileServiceTest {
        private Mock<IProfileService> profileService = null!;
        private ProfileModel active = null!;
        private GuideCameraProfileService guide = null!;

        [SetUp]
        public void SetUp() {
            active = new ProfileModel("Imaging Rig");
            profileService = new Mock<IProfileService>();
            profileService.SetupGet(s => s.ActiveProfile).Returns(() => active);
            guide = new GuideCameraProfileService(profileService.Object);
        }

        [TearDown]
        public void TearDown() {
            active.Dispose();
        }

        // Reflection, so a section upstream adds later is covered without touching this test.
        private static IEnumerable<PropertyInfo> OtherProfileProperties() {
            return typeof(IProfile).GetProperties().Where(p => p.Name != nameof(IProfile.CameraSettings));
        }

        [Test]
        public void CameraSettings_AreTheGuideCameraSettings() {
            Assert.That(guide.ActiveProfile.CameraSettings, Is.SameAs(active.GuideCameraSettings));
            Assert.That(guide.ActiveProfile.CameraSettings, Is.Not.SameAs(active.CameraSettings));
        }

        [TestCaseSource(nameof(OtherProfileProperties))]
        public void EveryOtherMember_IsTheProfilesOwn(PropertyInfo property) {
            var expected = property.GetValue(active);

            if (property.PropertyType.IsValueType || property.PropertyType == typeof(string)) {
                Assert.That(property.GetValue(guide.ActiveProfile), Is.EqualTo(expected));
            } else {
                Assert.That(property.GetValue(guide.ActiveProfile), Is.SameAs(expected));
            }
        }

        [Test]
        public void WritesThroughTheView_LandInTheGuideCameraSettings() {
            guide.ActiveProfile.CameraSettings.Gain = 300;
            guide.ActiveProfile.CameraSettings.IndiDriver = "indi_asi_ccd";

            Assert.That(active.GuideCameraSettings.Gain, Is.EqualTo(300));
            Assert.That(active.GuideCameraSettings.IndiDriver, Is.EqualTo("indi_asi_ccd"));
            Assert.That(active.CameraSettings.Gain, Is.Null);
            Assert.That(active.CameraSettings.IndiDriver, Is.Not.EqualTo("indi_asi_ccd"));
        }

        [Test]
        public void SettingCameraSettings_ReplacesTheGuideCameraSettings() {
            var imaging = active.CameraSettings;
            var replacement = new CameraSettings();

            guide.ActiveProfile.CameraSettings = replacement;

            Assert.That(active.GuideCameraSettings, Is.SameAs(replacement));
            Assert.That(active.CameraSettings, Is.SameAs(imaging));
        }

        [Test]
        public void ActiveProfile_FollowsAProfileSwitch() {
            var view = guide.ActiveProfile;
            using var next = new ProfileModel("Travel Rig");

            active = next;

            Assert.That(guide.ActiveProfile, Is.SameAs(view));
            Assert.That(view.Name, Is.EqualTo("Travel Rig"));
            Assert.That(view.CameraSettings, Is.SameAs(next.GuideCameraSettings));
        }

        [Test]
        public void DisposingTheView_LeavesTheProfileAlone() {
            guide.ActiveProfile.Dispose();

            active.GuideCameraSettings.PixelSize = 2.9d;
            Assert.That(guide.ActiveProfile.CameraSettings.PixelSize, Is.EqualTo(2.9d));
        }

        [Test]
        public void PropertyChanged_IsTheProfilesOwn() {
            var names = new List<string?>();
            guide.ActiveProfile.PropertyChanged += (_, e) => names.Add(e.PropertyName);

            active.GuideCameraSettings.Gain = 300;

            Assert.That(names, Is.EqualTo(new[] { "Settings" }));
        }

        [Test]
        public void ProfileChanged_IsRaisedByTheGuideService_WithGuideViewsOfBothProfiles() {
            using var next = new ProfileModel("Travel Rig");
            object? sender = null;
            EventArgs? args = null;
            guide.ProfileChanged += (s, e) => (sender, args) = (s, e);

            profileService.Raise(s => s.ProfileChanged += null, new ProfileChangedEventArgs(active, next));

            Assert.That(sender, Is.SameAs(guide));
            var changed = args as ProfileChangedEventArgs;
            Assert.That(changed, Is.Not.Null);
            Assert.That(changed!.OldProfile.Name, Is.EqualTo("Imaging Rig"));
            Assert.That(changed.OldProfile.CameraSettings, Is.SameAs(active.GuideCameraSettings));
            Assert.That(changed.NewProfile.Name, Is.EqualTo("Travel Rig"));
            Assert.That(changed.NewProfile.CameraSettings, Is.SameAs(next.GuideCameraSettings));
        }

        [Test]
        public void ProfileChanged_WithPlainEventArgs_IsPassedOn() {
            EventArgs? args = null;
            guide.ProfileChanged += (_, e) => args = e;

            profileService.Raise(s => s.ProfileChanged += null, EventArgs.Empty);

            Assert.That(args, Is.SameAs(EventArgs.Empty));
        }

        [TestCase(nameof(IProfileService.LocaleChanged))]
        [TestCase(nameof(IProfileService.LocationChanged))]
        [TestCase(nameof(IProfileService.BeforeProfileChanging))]
        [TestCase(nameof(IProfileService.HorizonChanged))]
        public void OtherEvents_AreRaisedByTheGuideService(string eventName) {
            var senders = new List<object?>();
            typeof(IProfileService).GetEvent(eventName)!.AddEventHandler(guide, new EventHandler((s, _) => senders.Add(s)));

            switch (eventName) {
                case nameof(IProfileService.LocaleChanged):
                    profileService.Raise(s => s.LocaleChanged += null, EventArgs.Empty);
                    break;
                case nameof(IProfileService.LocationChanged):
                    profileService.Raise(s => s.LocationChanged += null, EventArgs.Empty);
                    break;
                case nameof(IProfileService.BeforeProfileChanging):
                    profileService.Raise(s => s.BeforeProfileChanging += null, EventArgs.Empty);
                    break;
                case nameof(IProfileService.HorizonChanged):
                    profileService.Raise(s => s.HorizonChanged += null, EventArgs.Empty);
                    break;
            }

            Assert.That(senders, Is.EqualTo(new object[] { guide }));
        }

        [Test]
        public void ServiceCalls_GoToTheRealService() {
            var meta = new ProfileMeta();
            profileService.Setup(s => s.SelectProfile(meta)).Returns(true);

            Assert.That(guide.SelectProfile(meta), Is.True);
            guide.ChangeLatitude(48.1);

            profileService.Verify(s => s.SelectProfile(meta), Times.Once);
            profileService.Verify(s => s.ChangeLatitude(48.1), Times.Once);
        }
    }
}
