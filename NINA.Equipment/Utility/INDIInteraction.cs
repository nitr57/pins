#region "copyright"

/*
    Copyright © 2016 - 2025 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.INDI;
using NINA.INDI.Devices;
using NINA.INDI.Enums;
using NINA.Equipment.Equipment.MyFocuser;
using NINA.Equipment.Interfaces;
using NINA.Image.Interfaces;
using NINA.Profile.Interfaces;
using System;
using NINA.Core.Utility;
using System.Collections.Generic;
using System.Threading.Tasks;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Equipment.MyRotator;
using NINA.Equipment.Equipment.MyFilterWheel;
using NINA.Equipment.Equipment.MyFlatDevice;
using NINA.Equipment.Equipment.MyWeatherData;
using NINA.Equipment.Equipment.MySwitch;

namespace NINA.Equipment.Utility {

    public class INDIInteraction(IProfileService profileService) {
        private readonly IProfileService profileService = profileService;

        public List<ICamera> GetCameras(IExposureDataFactory exposureDataFactory) {
            var l = new List<ICamera>();
            return l;
        }

        public async Task<List<IFocuser>> GetFocusers() {
            var l = new List<IFocuser>();
            if (!await INDIClient.Instance.WaitForServerReadyAsync(TimeSpan.FromSeconds(15))) {
                Logger.Debug("INDI server not ready - skipping INDI focuser enumeration");
                return l;
            }

            // Fetch the INDI driver that is supposed to be used from profile
            string driver = profileService.ActiveProfile.FocuserSettings.IndiDriver;

            // Query devices for this driver
            foreach (var device in await INDIClient.Instance.GetDevices(DeviceInterface.FOCUSER_INTERFACE, driver)) {
                IndiFocuser focuser = new(device, profileService);
                l.Add(focuser);
            }
            return l;
        }

        public async Task<List<ITelescope>> GetTelescopes() {
            var l = new List<ITelescope>();
            if (!await INDIClient.Instance.WaitForServerReadyAsync(TimeSpan.FromSeconds(15))) {
                Logger.Debug("INDI server not ready - skipping INDI telescope enumeration");
                return l;
            }

            // Fetch the INDI driver that is supposed to be used from profile
            string driver = profileService.ActiveProfile.TelescopeSettings.IndiDriver;

            // Query devices for this driver
            foreach (var device in await INDIClient.Instance.GetDevices(DeviceInterface.TELESCOPE_INTERFACE, driver)) {
                IndiTelescope telescope = new(device, profileService);
                l.Add(telescope);
            }
            return l;
        }

        public async Task<List<IRotator>> GetRotators() {
            var l = new List<IRotator>();
            if (!await INDIClient.Instance.WaitForServerReadyAsync(TimeSpan.FromSeconds(15))) {
                Logger.Debug("INDI server not ready - skipping INDI rotator enumeration");
                return l;
            }

            // Fetch the INDI driver that is supposed to be used from profile
            string driver = profileService.ActiveProfile.RotatorSettings.IndiDriver;

            // Query devices for this driver
            foreach (var device in await INDIClient.Instance.GetDevices(DeviceInterface.ROTATOR_INTERFACE, driver)) {
                IndiRotator rotator = new(device, profileService);
                l.Add(rotator);
            }
            return l;
        }

        public async Task<List<IFilterWheel>> GetFilterWheels() {
            var l = new List<IFilterWheel>();
            if (!await INDIClient.Instance.WaitForServerReadyAsync(TimeSpan.FromSeconds(15))) {
                Logger.Debug("INDI server not ready - skipping INDI filterwheel enumeration");
                return l;
            }

            // Fetch the INDI driver that is supposed to be used from profile
            string driver = profileService.ActiveProfile.FilterWheelSettings.IndiDriver;

            // Query devices for this driver
            foreach (var device in await INDIClient.Instance.GetDevices(DeviceInterface.FILTER_INTERFACE, driver)) {
                IndiFilterWheel filterWheel = new(device, profileService);
                l.Add(filterWheel);
            }
            return l;
        }

        public async Task<List<IFlatDevice>> GetFlatDevices() {
            var l = new List<IFlatDevice>();
            if (!await INDIClient.Instance.WaitForServerReadyAsync(TimeSpan.FromSeconds(15))) {
                Logger.Debug("INDI server not ready - skipping INDI flat device enumeration");
                return l;
            }

            // Fetch the INDI driver that is supposed to be used from profile
            string driver = profileService.ActiveProfile.FlatDeviceSettings.IndiDriver;

            // Query devices for this driver
            foreach (var device in await INDIClient.Instance.GetDevices(DeviceInterface.LIGHTBOX_INTERFACE, driver)) {
                IndiFlatDevice flatDevice = new(device, profileService);
                l.Add(flatDevice);
            }
            return l;
        }

        public async Task<List<IWeatherData>> GetWeatherData() {
            var l = new List<IWeatherData>();
            if (!await INDIClient.Instance.WaitForServerReadyAsync(TimeSpan.FromSeconds(15))) {
                Logger.Debug("INDI server not ready - skipping INDI weather data enumeration");
                return l;
            }

            // Fetch the INDI driver that is supposed to be used from profile
            string driver = profileService.ActiveProfile.WeatherDataSettings.IndiDriver;

            // Query devices for this driver
            foreach (var device in await INDIClient.Instance.GetDevices(DeviceInterface.WEATHER_INTERFACE, driver)) {
                IndiWeatherData weatherData = new(device, profileService);
                l.Add(weatherData);
            }
            return l;
        }

        public async Task<List<ISwitchHub>> GetSwitches() {
            var l = new List<ISwitchHub>();
            if (!await INDIClient.Instance.WaitForServerReadyAsync(TimeSpan.FromSeconds(15))) {
                Logger.Debug("INDI server not ready - skipping INDI switch hub enumeration");
                return l;
            }

            string driver = profileService.ActiveProfile.SwitchSettings.IndiDriver;

            foreach (var device in await INDIClient.Instance.GetDevices(DeviceInterface.AUX_INTERFACE, driver)) {
                l.Add(new IndiSwitchHub(device, profileService));
            }
            return l;
        }

        public static string GetVersion() {
            return INDIClient.Instance.GetServerVersionString();
        }

        public static Version GetPlatformVersion() {
            return INDIClient.Instance.GetServerPlatformVersion();
        }
    }
}
