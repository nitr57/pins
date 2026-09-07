#region "copyright"

/*
    Copyright (c) 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using FluentAssertions;
using Moq;
using NINA.Core.Model.Equipment;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Equipment.MyCamera.ToupTekAlike;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Model;
using NINA.Image.ImageData;
using NINA.Profile.Interfaces;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ToupTek;

namespace NINA.Test.Equipment.Camera {

    [TestFixture]
    public class ToupTekAlikeCameraTest {
        private ImageDataFactoryTestUtility dataFactoryUtility;

        [SetUp]
        public void Setup() {
            dataFactoryUtility = new ImageDataFactoryTestUtility();
        }

        [Test]
        public async Task Connect_WhenInitializationFails_ReturnsFalseAndClosesSdk() {
            var sdk = CreateConnectableSdk();
            sdk.Setup(x => x.put_Option(ToupTekAlikeOption.OPTION_BITDEPTH, 1)).Returns(false);
            var sut = CreateCamera(sdk.Object, CreateProfileService().Object);

            var connected = await sut.Connect(default);

            connected.Should().BeFalse();
            sut.Connected.Should().BeFalse();
            sdk.Verify(x => x.Close(), Times.Once);
        }

        [Test]
        public async Task Connect_WithValidStoredTecTarget_DoesNotScaleSetpointTwice() {
            var sdk = CreateConnectableSdk();
            var target = -100;
            sdk.Setup(x => x.get_Option(ToupTekAlikeOption.OPTION_TECTARGET, out target));
            var sut = CreateCamera(
                sdk.Object,
                CreateProfileService().Object,
                flags: ToupTekAlikeFlag.FLAG_TRIGGER_SOFTWARE | ToupTekAlikeFlag.FLAG_TEC_ONOFF);

            var connected = await sut.Connect(default);
            sut.Disconnect();

            connected.Should().BeTrue();
            sdk.Verify(x => x.put_Option(ToupTekAlikeOption.OPTION_TECTARGET, -100), Times.Once);
            sdk.Verify(x => x.put_Option(ToupTekAlikeOption.OPTION_TECTARGET, -1000), Times.Never);
        }

        [Test]
        public void FanSpeed_ClampsValueBeforeSendingToSdk() {
            var sdk = new Mock<IToupTekAlikeCameraSDK>();
            sdk.SetupGet(x => x.Category).Returns("ToupTek");
            var currentFanSpeed = 0;
            sdk.Setup(x => x.get_Option(ToupTekAlikeOption.OPTION_FAN, out currentFanSpeed));
            var sut = CreateCamera(sdk.Object, CreateProfileService().Object, maxFanSpeed: 5);

            sut.FanSpeed = 99;

            sdk.Verify(x => x.put_Option(ToupTekAlikeOption.OPTION_FAN, 5), Times.Once);
            sdk.Verify(x => x.put_Option(ToupTekAlikeOption.OPTION_FAN, 99), Times.Never);
        }

        [Test]
        public void SetBinning_ClampsValuesBelowOne() {
            var sdk = new Mock<IToupTekAlikeCameraSDK>();
            sdk.SetupGet(x => x.Category).Returns("ToupTek");
            sdk.Setup(x => x.put_Option(It.IsAny<ToupTekAlikeOption>(), It.IsAny<int>())).Returns(true);
            var sut = CreateCamera(sdk.Object, CreateProfileService().Object);

            sut.SetBinning(0, 0);

            sdk.Verify(x => x.put_Option(ToupTekAlikeOption.OPTION_BINNING, 1), Times.Once);
        }

        [Test]
        public void ReadoutModeSelections_CanBeSetBeforeConnect() {
            var sdk = new Mock<IToupTekAlikeCameraSDK>();
            sdk.SetupGet(x => x.Category).Returns("ToupTek");
            var sut = CreateCamera(sdk.Object, CreateProfileService().Object);

            sut.ReadoutModeForNormalImages = 0;
            sut.ReadoutModeForSnapImages = 99;

            sut.ReadoutModeForNormalImages.Should().Be(0);
            sut.ReadoutModeForSnapImages.Should().Be(0);
        }

        [Test]
        public async Task Connect_WithFanSupport_ExposesFanSpeedAction() {
            var sdk = CreateConnectableSdk();
            var sut = CreateCamera(sdk.Object, CreateProfileService().Object, maxFanSpeed: 5);

            var connected = await sut.Connect(default);

            connected.Should().BeTrue();
            sut.SupportedActions.Should().Contain("Fan Speed");
        }

        [Test]
        public void ToupTekEnumExtensions_ConvertBetweenSharedAndNativeEnums() {
            ToupTekAlikeOption.OPTION_RAW.ToToupTek().Should().Be(ToupCam.eOPTION.OPTION_RAW);
            ToupTekAlikeAAF.AAF_GETPOSITION.ToToupTek().Should().Be(ToupCam.eAAF.AAF_GETPOSITION);
            ToupCam.eEVENT.EVENT_IMAGE.ToEvent().Should().Be(ToupTekAlikeEvent.EVENT_IMAGE);
        }

        [Test]
        public async Task StartExposure_AfterStrayFrame_FlushesBeforeTrigger() {
            // A frame that arrives while no exposure is waiting would otherwise sit in the SDK deque
            // and be returned as the next exposure's image.
            var h = await ConnectExposableCameraAsync();
            h.Camera.StartExposure(CreateBiasSequence());
            await CompleteExposureAsync(h);
            h.Callback(ToupTekAlikeEvent.EVENT_IMAGE);
            h.Calls.Clear();

            h.Camera.StartExposure(CreateBiasSequence());

            h.Calls.Should().ContainSingle(c => c == "OPTION_FLUSH=3");
            h.Calls.Should().ContainInOrder("OPTION_FLUSH=3", "Trigger(1)");
            h.Calls.Should().NotContain(c => c.StartsWith("OPTION_TRIGGER="));
        }

        [Test]
        public async Task StartExposure_AfterCleanExposure_DoesNotFlushOrRearm() {
            var h = await ConnectExposableCameraAsync();
            h.Camera.StartExposure(CreateBiasSequence());
            await CompleteExposureAsync(h);
            h.Calls.Clear();

            h.Camera.StartExposure(CreateBiasSequence());

            h.Calls.Should().NotContain(c => c.StartsWith("OPTION_FLUSH="));
            h.Calls.Should().NotContain(c => c.StartsWith("OPTION_TRIGGER="));
            h.Calls.Should().ContainSingle(c => c == "Trigger(1)");
        }

        [Test]
        public async Task DownloadExposure_AfterSuccessfulPull_DoesNotFlush() {
            var h = await ConnectExposableCameraAsync();
            h.Camera.StartExposure(CreateBiasSequence());
            h.Callback(ToupTekAlikeEvent.EVENT_IMAGE);
            h.Calls.Clear();

            var data = await h.Camera.DownloadExposure(default);

            data.Should().NotBeNull();
            h.Calls.Should().NotContain(c => c.StartsWith("OPTION_FLUSH="));
        }

        [Test]
        public async Task DownloadExposure_WhenPullFails_FlushesCamera() {
            var h = await ConnectExposableCameraAsync();
            var frameInfo = new ToupTekAlikeFrameInfo();
            h.Sdk.Setup(x => x.PullImage(It.IsAny<ushort[]>(), It.IsAny<int>(), out frameInfo)).Returns(false);
            h.Camera.StartExposure(CreateBiasSequence());
            h.Callback(ToupTekAlikeEvent.EVENT_IMAGE);
            h.Calls.Clear();

            var data = await h.Camera.DownloadExposure(default);

            data.Should().BeNull();
            h.Calls.Should().ContainSingle(c => c == "OPTION_FLUSH=3");
        }

        [Test]
        public async Task DownloadExposure_WhenAbortRacesDownload_ThrowsOperationCanceled() {
            // Abort landed after the image event but before the pull: the frame was flushed away.
            // That must surface as a cancellation, not as a failed download.
            var h = await ConnectExposableCameraAsync();
            h.Camera.StartExposure(CreateBiasSequence());
            h.Callback(ToupTekAlikeEvent.EVENT_IMAGE);
            var frameInfo = new ToupTekAlikeFrameInfo();
            h.Sdk.Setup(x => x.PullImage(It.IsAny<ushort[]>(), It.IsAny<int>(), out frameInfo)).Returns(false);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Func<Task> act = () => h.Camera.DownloadExposure(cts.Token);

            await act.Should().ThrowAsync<OperationCanceledException>();
        }

        [Test]
        public async Task StopExposure_FlushesAndNextStartExposureRearmsTriggerOnce() {
            var h = await ConnectExposableCameraAsync();
            h.Camera.StartExposure(CreateBiasSequence());
            h.Calls.Clear();

            h.Camera.StopExposure();

            h.Calls.Should().ContainInOrder("Trigger(0)", "OPTION_FLUSH=3");
            h.Calls.Should().NotContain(c => c.StartsWith("OPTION_TRIGGER="));

            h.Calls.Clear();
            h.Camera.StartExposure(CreateBiasSequence());

            h.Calls.Should().ContainSingle(c => c == "OPTION_TRIGGER=1");
            h.Calls.Should().ContainInOrder("OPTION_TRIGGER=1", "OPTION_FLUSH=3", "Trigger(1)");

            await CompleteExposureAsync(h);
            h.Calls.Clear();
            h.Camera.StartExposure(CreateBiasSequence());

            h.Calls.Should().NotContain(c => c.StartsWith("OPTION_TRIGGER="));
        }

        [Test]
        public async Task DownloadLiveView_KeepsSoftFlushAfterPull() {
            // In video mode frames keep arriving between pulls; without the flush every pull
            // returns the oldest queued frame.
            var h = await ConnectExposableCameraAsync();
            h.Camera.StartLiveView(CreateBiasSequence());
            h.Calls.Clear();
            h.Callback(ToupTekAlikeEvent.EVENT_IMAGE);

            var data = await h.Camera.DownloadLiveView(default);

            data.Should().NotBeNull();
            h.Calls.Should().ContainSingle(c => c == "OPTION_FLUSH=2");
            h.Calls.Should().NotContain(c => c == "OPTION_FLUSH=3");
        }

        [Test]
        public async Task StartLiveView_FlushesBeforeSwitchingToVideoMode() {
            var h = await ConnectExposableCameraAsync();
            h.Calls.Clear();

            h.Camera.StartLiveView(CreateBiasSequence());

            h.Calls.Should().ContainInOrder("OPTION_FLUSH=3", "OPTION_TRIGGER=0");
        }

        [Test]
        public async Task StopExposure_InLiveView_OnlyCancelsTrigger() {
            var h = await ConnectExposableCameraAsync();
            h.Camera.StartLiveView(CreateBiasSequence());
            h.Calls.Clear();

            h.Camera.StopExposure();

            h.Calls.Should().Equal("Trigger(0)");
        }


        [Test]
        public async Task StartExposure_AfterStopLiveView_FlushesLeftoverVideoFrame() {
            // The frame that completes the pending live view source after StopLiveView is never pulled;
            // without a flush it would be returned as the first exposure's image.
            var h = await ConnectExposableCameraAsync();
            h.Camera.StartLiveView(CreateBiasSequence());
            h.Callback(ToupTekAlikeEvent.EVENT_IMAGE);
            (await h.Camera.DownloadLiveView(default)).Should().NotBeNull(); // installs a fresh, uncompleted TCS

            h.Camera.StopLiveView();
            // The free-running frame the StopLiveView continuation is waiting on. Nobody pulls it.
            h.Callback(ToupTekAlikeEvent.EVENT_IMAGE);
            await WaitUntilAsync(() => !h.Camera.LiveViewEnabled);
            h.Calls.Should().Contain("OPTION_TRIGGER=1");
            h.Calls.Clear();

            h.Camera.StartExposure(CreateBiasSequence());

            h.Calls.Should().ContainInOrder("OPTION_FLUSH=3", "Trigger(1)");
        }

        [Test]
        public async Task StartExposure_StrayFrameBeforeTrigger_DoesNotCompleteExposure() {
            var h = await ConnectExposableCameraAsync();
            // Stray frame lands between the new TCS and the trigger (put_ExpoTime is one of the SDK
            // round-trips in that window).
            h.Sdk.Setup(x => x.put_ExpoTime(It.IsAny<uint>()))
                .Callback<uint>(_ => h.Callback(ToupTekAlikeEvent.EVENT_IMAGE))
                .Returns(true);

            h.Camera.StartExposure(CreateBiasSequence());
            var download = h.Camera.DownloadExposure(default);
            await Task.Delay(200);

            download.IsCompleted.Should().BeFalse("the stray frame must not satisfy the exposure that was triggered after it");
            h.Calls.Should().ContainInOrder("OPTION_FLUSH=3", "Trigger(1)");

            h.Callback(ToupTekAlikeEvent.EVENT_IMAGE);
            (await download).Should().NotBeNull();
        }

        [Test]
        public async Task StartLiveView_StrayFrameBeforeVideoMode_DoesNotCompleteFirstLiveViewFrame() {
            var h = await ConnectExposableCameraAsync();
            h.Sdk.Setup(x => x.put_ExpoTime(It.IsAny<uint>()))
                .Callback<uint>(_ => h.Callback(ToupTekAlikeEvent.EVENT_IMAGE))
                .Returns(true);

            h.Camera.StartLiveView(CreateBiasSequence());
            var download = h.Camera.DownloadLiveView(default);
            await Task.Delay(200);

            download.IsCompleted.Should().BeFalse("the stray frame was flushed before video mode and must not satisfy the first live view frame");
            h.Calls.Should().ContainInOrder("OPTION_FLUSH=3", "OPTION_TRIGGER=0");

            h.Callback(ToupTekAlikeEvent.EVENT_IMAGE);
            (await download).Should().NotBeNull();
        }

        private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 2000) {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (!condition()) {
                if (DateTime.UtcNow > deadline) {
                    throw new TimeoutException("condition not met");
                }
                await Task.Delay(10);
            }
        }

        private sealed class ExposureHarness {
            public Mock<IToupTekAlikeCameraSDK> Sdk = null!;
            public ToupTekAlikeCamera Camera = null!;
            public ToupTekAlikeCallback Callback = null!;
            public List<string> Calls = new List<string>();
        }

        private async Task<ExposureHarness> ConnectExposableCameraAsync() {
            var h = new ExposureHarness();
            h.Sdk = CreateExposableSdk(h.Calls, cb => h.Callback = cb);
            h.Camera = CreateCamera(h.Sdk.Object, CreateProfileService().Object);
            (await h.Camera.Connect(default)).Should().BeTrue();
            h.Callback.Should().NotBeNull();
            h.Calls.Clear();
            return h;
        }

        private static async Task CompleteExposureAsync(ExposureHarness h) {
            h.Callback(ToupTekAlikeEvent.EVENT_IMAGE);
            (await h.Camera.DownloadExposure(default)).Should().NotBeNull();
        }

        private static CaptureSequence CreateBiasSequence() {
            return new CaptureSequence(0.001, CaptureSequence.ImageTypes.BIAS, null, new BinningMode(1, 1), 1);
        }

        private static Mock<IToupTekAlikeCameraSDK> CreateExposableSdk(List<string> calls, Action<ToupTekAlikeCallback> captureCallback) {
            var sdk = CreateConnectableSdk();
            sdk.Setup(x => x.put_Option(It.IsAny<ToupTekAlikeOption>(), It.IsAny<int>()))
                .Callback<ToupTekAlikeOption, int>((option, value) => calls.Add($"{option}={value}"))
                .Returns(true);
            sdk.Setup(x => x.Trigger(It.IsAny<ushort>()))
                .Callback<ushort>(count => calls.Add($"Trigger({count})"))
                .Returns(true);
            sdk.Setup(x => x.put_ROI(It.IsAny<uint>(), It.IsAny<uint>(), It.IsAny<uint>(), It.IsAny<uint>())).Returns(true);
            sdk.Setup(x => x.put_ExpoTime(It.IsAny<uint>())).Returns(true);
            sdk.Setup(x => x.StartPullModeWithCallback(It.IsAny<ToupTekAlikeCallback>()))
                .Callback<ToupTekAlikeCallback>(cb => captureCallback(cb))
                .Returns(true);

            // BinX reads back from the SDK, and PullImage divides the frame size by it.
            var binning = 1;
            sdk.Setup(x => x.get_Option(ToupTekAlikeOption.OPTION_BINNING, out binning));

            var frameInfo = new ToupTekAlikeFrameInfo();
            sdk.Setup(x => x.PullImage(It.IsAny<ushort[]>(), It.IsAny<int>(), out frameInfo)).Returns(true);
            return sdk;
        }

        private ToupTekAlikeCamera CreateCamera(
            IToupTekAlikeCameraSDK sdk,
            IProfileService profileService,
            ToupTekAlikeFlag flags = ToupTekAlikeFlag.FLAG_TRIGGER_SOFTWARE,
            uint maxFanSpeed = 0) {
            return new ToupTekAlikeCamera(
                CreateDeviceInfo(flags, maxFanSpeed),
                sdk,
                profileService,
                dataFactoryUtility.ExposureDataFactory);
        }

        private static ToupTekAlikeDeviceInfo CreateDeviceInfo(
            ToupTekAlikeFlag flags = ToupTekAlikeFlag.FLAG_TRIGGER_SOFTWARE,
            uint maxFanSpeed = 0) {
            return new ToupTekAlikeDeviceInfo {
                displayname = "ToupTek Test Camera",
                id = @"vid_1234&pid_abcd#camera",
                model = new ToupTekAlikeModel {
                    flag = (ulong)flags,
                    maxfanspeed = maxFanSpeed,
                    xpixsz = 3.76f,
                    ypixsz = 3.76f
                }
            };
        }

        private static Mock<IProfileService> CreateProfileService() {
            var cameraSettings = new Mock<ICameraSettings>();
            cameraSettings.SetupProperty(x => x.BitScaling, false);
            cameraSettings.SetupProperty(x => x.BinAverageEnabled, false);
            cameraSettings.SetupProperty(x => x.TouptekAlikeDewHeaterStrength, -1);
            cameraSettings.SetupProperty(x => x.TouptekAlikeUltraMode, false);
            cameraSettings.SetupProperty(x => x.TouptekAlikeHighFullwell, false);
            cameraSettings.SetupProperty(x => x.TouptekAlikeLEDLights, false);

            var profile = new Mock<IProfile>();
            profile.SetupGet(x => x.CameraSettings).Returns(cameraSettings.Object);

            var profileService = new Mock<IProfileService>();
            profileService.SetupGet(x => x.ActiveProfile).Returns(profile.Object);
            return profileService;
        }

        private static Mock<IToupTekAlikeCameraSDK> CreateConnectableSdk() {
            var sdk = new Mock<IToupTekAlikeCameraSDK>();
            sdk.SetupGet(x => x.Category).Returns("ToupTek");
            sdk.Setup(x => x.Open(It.IsAny<string>())).Returns(sdk.Object);
            sdk.Setup(x => x.put_Option(It.IsAny<ToupTekAlikeOption>(), It.IsAny<int>())).Returns(true);
            sdk.Setup(x => x.put_AutoExpoEnable(false)).Returns(true);
            sdk.Setup(x => x.StartPullModeWithCallback(It.IsAny<ToupTekAlikeCallback>())).Returns(true);
            sdk.SetupGet(x => x.MonoMode).Returns(false);

            var width = 1920;
            var height = 1080;
            sdk.Setup(x => x.get_Size(out width, out height));

            var fourCC = BitConverter.ToUInt32(Encoding.ASCII.GetBytes("RGGB"), 0);
            uint bitDepth = 12;
            sdk.Setup(x => x.get_RawFormat(out fourCC, out bitDepth)).Returns(true);

            return sdk;
        }
    }
}
