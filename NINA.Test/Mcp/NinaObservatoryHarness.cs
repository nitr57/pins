// SPDX-License-Identifier: MPL-2.0

using Moq;
using NINA.Core.Utility.WindowService;
using NINA.Equipment.Equipment.MyDome;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Mcp.Adapter;
using NINA.Mcp.Adapter.Sequence;
using NINA.PlateSolving.Interfaces;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Interfaces.Mediator;
using NINA.ViewModel.Sequencer;
using NINA.WPF.Base.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.ViewModel;

namespace NINA.Test.Mcp {

    /// <summary>
    /// A <see cref="NinaObservatory"/> on Moq mediators. Mocks without setups return defaults; the profile mock returns
    /// nested mocks (polling interval 0, i.e. the shortest waits).
    /// </summary>
    internal sealed class NinaObservatoryHarness {

        public NinaObservatoryHarness() {
            Profile = new Mock<IProfileService> { DefaultValue = DefaultValue.Mock };

            Observatory = new NinaObservatory(
                Camera.Object,
                Telescope.Object,
                Focuser.Object,
                FilterWheel.Object,
                Guider.Object,
                Rotator.Object,
                FlatDevice.Object,
                Dome.Object,
                Switch.Object,
                Weather.Object,
                SafetyMonitor.Object,
                Mock.Of<IImagingMediator>(),
                Mock.Of<IImageSaveMediator>(),
                Mock.Of<IImageHistoryVM>(),
                Mock.Of<IAutoFocusVMFactory>(),
                Mock.Of<IWindowServiceFactory>(),
                Mock.Of<IApplicationStatusMediator>(),
                Profile.Object,
                Mock.Of<IPlateSolverFactory>(),
                DomeFollower.Object,
                Mock.Of<IAdvancedGuider>(),
                new NinaSequenceService(
                    Mock.Of<ISequenceMediator>(),
                    Mock.Of<ISequenceNavigationVM>(),
                    Profile.Object,
                    Camera.Object,
                    Rotator.Object));
        }

        public NinaObservatory Observatory { get; }

        public Mock<IProfileService> Profile { get; }

        public Mock<ICameraMediator> Camera { get; } = new();

        public Mock<ITelescopeMediator> Telescope { get; } = new();

        public Mock<IFocuserMediator> Focuser { get; } = new();

        public Mock<IFilterWheelMediator> FilterWheel { get; } = new();

        public Mock<IGuiderMediator> Guider { get; } = new();

        public Mock<IRotatorMediator> Rotator { get; } = new();

        public Mock<IFlatDeviceMediator> FlatDevice { get; } = new();

        public Mock<IDomeMediator> Dome { get; } = new();

        public Mock<ISwitchMediator> Switch { get; } = new();

        public Mock<IWeatherDataMediator> Weather { get; } = new();

        public Mock<ISafetyMonitorMediator> SafetyMonitor { get; } = new();

        public Mock<IDomeFollower> DomeFollower { get; } = new();

        /// <summary>A connected dome with a controllable shutter, reporting <paramref name="shutter"/>.</summary>
        public DomeInfo ConnectDome(ShutterState shutter) {
            var info = new DomeInfo {
                Connected = true,
                CanSetShutter = true,
                CanPark = true,
                CanFindHome = true,
                CanSetAzimuth = true,
                ShutterStatus = shutter
            };

            Dome.Setup(mediator => mediator.GetInfo()).Returns(info);

            return info;
        }
    }
}
