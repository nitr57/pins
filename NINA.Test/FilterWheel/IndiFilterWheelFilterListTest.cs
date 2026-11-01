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
using NINA.Core.Model.Equipment;
using NINA.Core.Utility;
using NINA.Equipment.Equipment;
using NINA.Equipment.Equipment.MyFilterWheel;
using NINA.INDI;
using NINA.INDI.Interfaces;
using NINA.Profile.Interfaces;
using NUnit.Framework;
using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace NINA.Test.FilterWheel {

    /// <summary>
    /// At connect the INDI filter wheel matches the profile's filter list to the driver's filter names. Connect waits only for
    /// FILTER_SLOT, so the names can still be missing; then the list must stay as it is instead of being emptied.
    /// </summary>
    [TestFixture]
    public class IndiFilterWheelFilterListTest {
        private Mock<IINDIFilterWheel> mockDevice = null!;
        private ObserveAllCollection<FilterInfo> profileFilters = null!;
        private Mock<IProfileService> profileService = null!;
        private string[] deviceNames = [];

        [SetUp]
        public void Init() {
            deviceNames = [];
            mockDevice = new Mock<IINDIFilterWheel>();
            mockDevice.SetupGet(d => d.Names).Returns(() => deviceNames);
            mockDevice.SetupGet(d => d.FocusOffsets).Returns(() => new int[deviceNames.Length]);

            profileFilters = new ObserveAllCollection<FilterInfo>();
            for (short i = 0; i < 5; i++) {
                profileFilters.Add(new FilterInfo($"Profile {i}", 100 + i, i));
            }

            var settings = new Mock<IFilterWheelSettings>();
            settings.SetupProperty(s => s.FilterWheelFilters, profileFilters);
            var profile = new Mock<IProfile>();
            profile.SetupGet(p => p.FilterWheelSettings).Returns(settings.Object);
            profileService = new Mock<IProfileService>();
            profileService.SetupGet(p => p.ActiveProfile).Returns(profile.Object);
        }

        private IndiFilterWheel CreateConnectedSUT(TimeSpan namesWait) {
            var sut = new IndiFilterWheel(new INDIDeviceInfo { Id = "Test" }, profileService.Object) { FilterNamesWait = namesWait };
            typeof(IndiDevice<IINDIFilterWheel>)
                .GetField("device", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(sut, mockDevice.Object);
            typeof(IndiDevice<IINDIFilterWheel>)
                .GetField("connectedExpectation", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(sut, true);
            return sut;
        }

        private static Task PostConnect(IndiFilterWheel sut) {
            return (Task)typeof(IndiFilterWheel)
                .GetMethod("PostConnect", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(sut, null)!;
        }

        private ObserveAllCollection<FilterInfo> Filters => profileService.Object.ActiveProfile.FilterWheelSettings.FilterWheelFilters;

        [Test]
        public async Task WithoutFilterNames_TheProfilesFilterListStaysAsItIs() {
            var sut = CreateConnectedSUT(TimeSpan.FromMilliseconds(200));

            await PostConnect(sut);

            Assert.That(Filters.Select(f => f.Name), Is.EqualTo(new[] { "Profile 0", "Profile 1", "Profile 2", "Profile 3", "Profile 4" }));
            Assert.That(Filters.Select(f => f.FocusOffset), Is.EqualTo(new[] { 100, 101, 102, 103, 104 }));
        }

        [Test]
        public async Task FilterNamesThatArriveLate_AreWaitedFor() {
            var sut = CreateConnectedSUT(TimeSpan.FromSeconds(5));
            var connect = PostConnect(sut);

            await Task.Delay(300);
            deviceNames = ["L", "R", "G"];
            await connect.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.That(Filters.Select(f => f.Name), Is.EqualTo(new[] { "Profile 0", "Profile 1", "Profile 2" }),
                "a wheel with three slots keeps the profile's first three filters");
        }

        [Test]
        public async Task MoreSlotsThanTheProfileHas_ImportsTheMissingFilters() {
            profileFilters.RemoveAt(4);
            profileFilters.RemoveAt(3);
            deviceNames = ["L", "R", "G", "B", "Ha"];
            var sut = CreateConnectedSUT(TimeSpan.FromSeconds(5));

            await PostConnect(sut);

            Assert.That(Filters.Select(f => f.Name), Is.EqualTo(new[] { "Profile 0", "Profile 1", "Profile 2", "B", "Ha" }));
        }
    }
}
