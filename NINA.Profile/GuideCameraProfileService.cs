#region "copyright"

/*
    Copyright © 2026 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Core.Utility;
using NINA.Profile.Interfaces;
using System;
using System.ComponentModel;
using System.Globalization;

namespace NINA.Profile {

    /// <summary>
    /// The profile service as the guide camera sees it: the active profile's GuideCameraSettings take the
    /// place of its CameraSettings, everything else is the real profile service.
    /// <para>
    /// Camera classes, PersistSettingsCameraDecorator, CameraVM and CameraChooserVM read and write their
    /// settings only through ActiveProfile.CameraSettings, so a second camera stack built with this service
    /// keeps its device choice, gain, binning, driver and so on apart from the imaging camera's.
    /// </para>
    /// </summary>
    public class GuideCameraProfileService : IProfileService {
        private readonly IProfileService profileService;

        public GuideCameraProfileService(IProfileService profileService) {
            this.profileService = profileService;
            ActiveProfile = new GuideCameraProfileView(() => profileService.ActiveProfile);

            profileService.LocaleChanged += (_, e) => LocaleChanged?.Invoke(this, e);
            profileService.LocationChanged += (_, e) => LocationChanged?.Invoke(this, e);
            profileService.BeforeProfileChanging += (_, e) => BeforeProfileChanging?.Invoke(this, e);
            profileService.HorizonChanged += (_, e) => HorizonChanged?.Invoke(this, e);
            profileService.ProfileChanged += (_, e) => ProfileChanged?.Invoke(this, ViewOf(e));
        }

        /// <summary>Always the same object; it follows the real service's active profile.</summary>
        public IProfile ActiveProfile { get; }

        public bool ProfileWasSpecifiedFromCommandLineArgs => profileService.ProfileWasSpecifiedFromCommandLineArgs;
        public AsyncObservableCollection<ProfileMeta> Profiles => profileService.Profiles;

        public bool Clone(ProfileMeta profileInfos) => profileService.Clone(profileInfos);
        public void Add() => profileService.Add();
        public bool SelectProfile(ProfileMeta profileInfo) => profileService.SelectProfile(profileInfo);
        public bool RemoveProfile(ProfileMeta profileInfo) => profileService.RemoveProfile(profileInfo);
        public void ChangeLocale(CultureInfo language) => profileService.ChangeLocale(language);
        public void ChangeLatitude(double latitude) => profileService.ChangeLatitude(latitude);
        public void ChangeLongitude(double longitude) => profileService.ChangeLongitude(longitude);
        public void ChangeElevation(double elevation) => profileService.ChangeElevation(elevation);
        public void ChangeHorizon(string horizonFilePath) => profileService.ChangeHorizon(horizonFilePath);
        public void Release() => profileService.Release();

        public event EventHandler LocaleChanged;
        public event EventHandler LocationChanged;
        public event EventHandler BeforeProfileChanging;
        public event EventHandler ProfileChanged;
        public event EventHandler HorizonChanged;

        // Listeners such as GuiderVM read the profiles from the event, so they must get guide views too.
        private static EventArgs ViewOf(EventArgs e) {
            if (e is ProfileChangedEventArgs changed) {
                return new ProfileChangedEventArgs(ViewOf(changed.OldProfile), ViewOf(changed.NewProfile));
            }
            return e;
        }

        private static IProfile ViewOf(IProfile profile) {
            return profile == null ? null : new GuideCameraProfileView(() => profile);
        }

        /// <summary>
        /// A profile whose CameraSettings are the guide camera's. Every other member is the profile's own.
        /// </summary>
        private sealed class GuideCameraProfileView(Func<IProfile> profile) : IProfile {
            private IProfile P => profile();

            public ICameraSettings CameraSettings { get => P.GuideCameraSettings; set => P.GuideCameraSettings = value; }
            public ICameraSettings GuideCameraSettings { get => P.GuideCameraSettings; set => P.GuideCameraSettings = value; }

            public Guid Id { get => P.Id; set => P.Id = value; }
            public string Name { get => P.Name; set => P.Name = value; }
            public string Description => P.Description;
            public string Location => P.Location;
            public DateTime LastUsed => P.LastUsed;
            public IApplicationSettings ApplicationSettings { get => P.ApplicationSettings; set => P.ApplicationSettings = value; }
            public IAstrometrySettings AstrometrySettings { get => P.AstrometrySettings; set => P.AstrometrySettings = value; }
            public IColorSchemaSettings ColorSchemaSettings { get => P.ColorSchemaSettings; set => P.ColorSchemaSettings = value; }
            public IDomeSettings DomeSettings { get => P.DomeSettings; set => P.DomeSettings = value; }
            public IFilterWheelSettings FilterWheelSettings { get => P.FilterWheelSettings; set => P.FilterWheelSettings = value; }
            public IFlatWizardSettings FlatWizardSettings { get => P.FlatWizardSettings; set => P.FlatWizardSettings = value; }
            public IFocuserSettings FocuserSettings { get => P.FocuserSettings; set => P.FocuserSettings = value; }
            public IFramingAssistantSettings FramingAssistantSettings { get => P.FramingAssistantSettings; set => P.FramingAssistantSettings = value; }
            public IGuiderSettings GuiderSettings { get => P.GuiderSettings; set => P.GuiderSettings = value; }
            public IImageFileSettings ImageFileSettings { get => P.ImageFileSettings; set => P.ImageFileSettings = value; }
            public IImageSettings ImageSettings { get => P.ImageSettings; set => P.ImageSettings = value; }
            public IMeridianFlipSettings MeridianFlipSettings { get => P.MeridianFlipSettings; set => P.MeridianFlipSettings = value; }
            public IPlanetariumSettings PlanetariumSettings { get => P.PlanetariumSettings; set => P.PlanetariumSettings = value; }
            public IPlateSolveSettings PlateSolveSettings { get => P.PlateSolveSettings; set => P.PlateSolveSettings = value; }
            public IRotatorSettings RotatorSettings { get => P.RotatorSettings; set => P.RotatorSettings = value; }
            public IFlatDeviceSettings FlatDeviceSettings { get => P.FlatDeviceSettings; set => P.FlatDeviceSettings = value; }
            public ISequenceSettings SequenceSettings { get => P.SequenceSettings; set => P.SequenceSettings = value; }
            public ISwitchSettings SwitchSettings { get => P.SwitchSettings; set => P.SwitchSettings = value; }
            public ITelescopeSettings TelescopeSettings { get => P.TelescopeSettings; set => P.TelescopeSettings = value; }
            public IWeatherDataSettings WeatherDataSettings { get => P.WeatherDataSettings; set => P.WeatherDataSettings = value; }
            public ISnapShotControlSettings SnapShotControlSettings { get => P.SnapShotControlSettings; set => P.SnapShotControlSettings = value; }
            public ISafetyMonitorSettings SafetyMonitorSettings { get => P.SafetyMonitorSettings; set => P.SafetyMonitorSettings = value; }
            public IPluginSettings PluginSettings { get => P.PluginSettings; set => P.PluginSettings = value; }
            public IGnssSettings GnssSettings { get => P.GnssSettings; set => P.GnssSettings = value; }
            public IAlpacaSettings AlpacaSettings { get => P.AlpacaSettings; set => P.AlpacaSettings = value; }
            public IImageHistorySettings ImageHistorySettings { get => P.ImageHistorySettings; set => P.ImageHistorySettings = value; }
            public IDockPanelSettings DockPanelSettings { get => P.DockPanelSettings; set => P.DockPanelSettings = value; }

            public void Save() => P.Save();

            // The profile belongs to the real profile service.
            public void Dispose() { }

            // Attaches to the profile active at subscription time; nothing in the camera path subscribes here.
            public event PropertyChangedEventHandler PropertyChanged {
                add => P.PropertyChanged += value;
                remove => P.PropertyChanged -= value;
            }
        }
    }
}
