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
using NINA.Equipment.Interfaces;
using NINA.Equipment.Utility;
using NINA.Image.Interfaces;
using NINA.INDI;
using NINA.INDI.Enums;
using NINA.Profile;
using NINA.Profile.Interfaces;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using ProfileModel = NINA.Profile.Profile;

namespace NINA.Test.INDI {

    /// <summary>
    /// The guide camera list runs INDIInteraction with the guide camera's profile service and the "GuideCamera"
    /// category, so it loads the guide camera's driver next to the imaging camera's. Runs against a fake indiserver.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class INDIInteractionGuideCameraTest {
        private const string ToupTek = "indi_toupcam_ccd";
        private const string Asi = "indi_asi_ccd";
        private const string PlayerOne = "indi_playerone_ccd";
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

        private FakeIndiServer server = null!;
        private INDIClient client = null!;
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Structure", "NUnit1032", Justification = "Borrowed singleton, restored in TearDown")]
        private INDIClient? previousInstance;
        private FakeIndiServer.Connection main = null!;
        private readonly List<string> commands = [];
        private ProfileModel profile = null!;
        private IProfileService imaging = null!;
        private GuideCameraProfileService guide = null!;

        [SetUp]
        public async Task SetUp() {
            lock (commands) {
                commands.Clear();
            }
            server = new FakeIndiServer();
            client = new INDIClient(server.Port, startServer: false) { FifoCommandsForTests = OnFifoCommand };
            previousInstance = INDIClient.SetInstanceForTests(client);
            Assert.That(await client.Connect(), Is.True);
            main = await server.NextConnectionAsync(Timeout);

            profile = new ProfileModel("Rig");
            profile.CameraSettings.IndiDriver = ToupTek;
            profile.GuideCameraSettings.IndiDriver = Asi;
            var profileService = new Mock<IProfileService>();
            profileService.SetupGet(s => s.ActiveProfile).Returns(profile);
            imaging = profileService.Object;
            guide = new GuideCameraProfileService(imaging);
        }

        [TearDown]
        public void TearDown() {
            client.Dispose();
            // Never restore null, see INDITelescopePulseGuideTest.
            INDIClient.SetInstanceForTests(previousInstance ?? client);
            server.Dispose();
            profile.Dispose();
        }

        [Test]
        public async Task GuideList_LoadsTheGuideCamerasDriver_NextToTheImagingCamerasDriver() {
            await ImagingScanAsync();

            var cameras = await GuideScanAsync();

            Assert.That(Commands(), Is.EqualTo(new[] { $"start {ToupTek}", $"start {Asi}" }));
            Assert.That(cameras.Select(c => c.Id), Does.Contain(DeviceOf(Asi)));
        }

        [Test]
        public async Task GuideCameras_UseTheGuideCamerasSettings() {
            var cameras = await GuideScanAsync();

            Assert.That(cameras, Is.Not.Empty);
            Assert.That(cameras.Select(ProfileServiceOf), Is.All.SameAs(guide));
        }

        [Test]
        public async Task ImagingRescan_KeepsTheGuideCamerasDriver() {
            await ImagingScanAsync();
            await GuideScanAsync();

            await ImagingScanAsync();

            Assert.That(Commands(), Is.EqualTo(new[] { $"start {ToupTek}", $"start {Asi}" }));
        }

        [Test]
        public async Task GuideDriverSwitch_LeavesTheImagingCamerasDriverLoaded() {
            await ImagingScanAsync();
            await GuideScanAsync();

            profile.GuideCameraSettings.IndiDriver = PlayerOne;
            await GuideScanAsync();

            Assert.That(Commands(), Is.EqualTo(new[] { $"start {ToupTek}", $"start {Asi}", $"stop {Asi}", $"start {PlayerOne}" }));
        }

        private Task<List<ICamera>> ImagingScanAsync() {
            return new INDIInteraction(imaging).GetCameras(Mock.Of<IExposureDataFactory>(), Mock.Of<IImageDataFactory>());
        }

        private Task<List<ICamera>> GuideScanAsync() {
            return new INDIInteraction(guide).GetCameras(Mock.Of<IExposureDataFactory>(), Mock.Of<IImageDataFactory>(), "GuideCamera");
        }

        private static object? ProfileServiceOf(ICamera camera) {
            return camera.GetType().GetField("profileService", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(camera);
        }

        private List<string> Commands() {
            lock (commands) {
                return [.. commands];
            }
        }

        private static string DeviceOf(string driver) => $"{driver} device";

        // Plays indiserver: a started driver describes its device, a stopped driver deletes it.
        private void OnFifoCommand(string command) {
            lock (commands) {
                commands.Add(command);
            }
            var parts = command.Split(' ', 2);
            var device = DeviceOf(parts[1]);
            if (parts[0] == "start") {
                main.Send(
                    $"<defTextVector device=\"{device}\" name=\"DRIVER_INFO\" label=\"Driver Info\" group=\"General Info\" state=\"Idle\" perm=\"ro\" timeout=\"0\">"
                    + $"<defText name=\"DRIVER_NAME\" label=\"Name\">{device}</defText>"
                    + $"<defText name=\"DRIVER_EXEC\" label=\"Exec\">{parts[1]}</defText>"
                    + "<defText name=\"DRIVER_VERSION\" label=\"Version\">1.0</defText>"
                    + $"<defText name=\"DRIVER_INTERFACE\" label=\"Interface\">{(int)DeviceInterface.CCD_INTERFACE}</defText></defTextVector>\n");
            } else {
                main.Send($"<delProperty device=\"{device}\" />\n");
            }
        }
    }
}
