#region "copyright"

/*
    Copyright © 2025-2026 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.INDI;
using NINA.INDI.Devices;
using NINA.INDI.Enums;
using NINA.Equipment.Equipment.MyFocuser;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Interfaces;
using NINA.Image.Interfaces;
using NINA.Profile.Interfaces;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Equipment.MyRotator;
using NINA.Equipment.Equipment.MyFilterWheel;
using NINA.Equipment.Equipment.MyFlatDevice;
using NINA.Equipment.Equipment.MyWeatherData;
using NINA.Equipment.Equipment.MySwitch;
using NINA.Equipment.Equipment.MySafetyMonitor;
using NINA.Equipment.Equipment.MyDome;

namespace NINA.Equipment.Utility {

    public class INDIInteraction(IProfileService profileService) {
        private readonly IProfileService profileService = profileService;

        // Server readiness is awaited (bounded) inside INDIClient.GetDevices, so the
        // enumeration methods below can call it directly.

        public async Task<List<ICamera>> GetCameras(IExposureDataFactory exposureDataFactory, IImageDataFactory imageDataFactory) {
            var l = new List<ICamera>();

            // Fetch the INDI driver that is supposed to be used from profile
            string driver = profileService.ActiveProfile.CameraSettings.IndiDriver;

            // Query devices for this driver
            foreach (var device in await INDIClient.Instance.GetDevices(DeviceInterface.CCD_INTERFACE, driver, "Camera")) {
                l.Add(new IndiCamera(device, profileService, exposureDataFactory, imageDataFactory));
            }
            return l;
        }

        public async Task<List<IFocuser>> GetFocusers() {
            var l = new List<IFocuser>();

            // Fetch the INDI driver that is supposed to be used from profile
            string driver = profileService.ActiveProfile.FocuserSettings.IndiDriver;

            // Query devices for this driver
            foreach (var device in await INDIClient.Instance.GetDevices(DeviceInterface.FOCUSER_INTERFACE, driver, "Focuser")) {
                IndiFocuser focuser = new(device, profileService);
                l.Add(focuser);
            }
            return l;
        }

        public async Task<List<ITelescope>> GetTelescopes() {
            var l = new List<ITelescope>();

            // Fetch the INDI driver that is supposed to be used from profile
            string driver = profileService.ActiveProfile.TelescopeSettings.IndiDriver;

            // Query devices for this driver
            foreach (var device in await INDIClient.Instance.GetDevices(DeviceInterface.TELESCOPE_INTERFACE, driver, "Telescope")) {
                IndiTelescope telescope = new(device, profileService);
                l.Add(telescope);
            }
            return l;
        }

        public async Task<List<IRotator>> GetRotators() {
            var l = new List<IRotator>();

            // Fetch the INDI driver that is supposed to be used from profile
            string driver = profileService.ActiveProfile.RotatorSettings.IndiDriver;

            // Query devices for this driver
            foreach (var device in await INDIClient.Instance.GetDevices(DeviceInterface.ROTATOR_INTERFACE, driver, "Rotator")) {
                IndiRotator rotator = new(device, profileService);
                l.Add(rotator);
            }
            return l;
        }

        public async Task<List<IFilterWheel>> GetFilterWheels() {
            var l = new List<IFilterWheel>();

            // Fetch the INDI driver that is supposed to be used from profile
            string driver = profileService.ActiveProfile.FilterWheelSettings.IndiDriver;

            // Query devices for this driver
            foreach (var device in await INDIClient.Instance.GetDevices(DeviceInterface.FILTER_INTERFACE, driver, "FilterWheel")) {
                IndiFilterWheel filterWheel = new(device, profileService);
                l.Add(filterWheel);
            }
            return l;
        }

        public async Task<List<IFlatDevice>> GetFlatDevices() {
            var l = new List<IFlatDevice>();

            // Fetch the INDI driver that is supposed to be used from profile
            string driver = profileService.ActiveProfile.FlatDeviceSettings.IndiDriver;

            // Query devices for this driver. A flat device can be a light box, a dust cap or both,
            // and drivers that advertise only the cover are still a flat device to NINA.
            var flatInterfaces = DeviceInterface.LIGHTBOX_INTERFACE | DeviceInterface.DUSTCAP_INTERFACE;
            foreach (var device in await INDIClient.Instance.GetDevices(flatInterfaces, driver, "FlatDevice")) {
                IndiFlatDevice flatDevice = new(device, profileService);
                l.Add(flatDevice);
            }
            return l;
        }

        public async Task<List<IWeatherData>> GetWeatherData() {
            var l = new List<IWeatherData>();

            // Fetch the INDI driver that is supposed to be used from profile
            string driver = profileService.ActiveProfile.WeatherDataSettings.IndiDriver;

            // Query devices for this driver
            foreach (var device in await INDIClient.Instance.GetDevices(DeviceInterface.WEATHER_INTERFACE, driver, "WeatherData")) {
                IndiWeatherData weatherData = new(device, profileService);
                l.Add(weatherData);
            }
            return l;
        }

        public async Task<List<ISwitchHub>> GetSwitches() {
            var l = new List<ISwitchHub>();

            // Fetch the INDI driver that is supposed to be used from profile
            string driver = profileService.ActiveProfile.SwitchSettings.IndiDriver;

            // Query devices for this driver
            foreach (var device in await INDIClient.Instance.GetDevices(DeviceInterface.AUX_INTERFACE, driver, "Switch")) {
                l.Add(new IndiSwitchHub(device, profileService));
            }
            return l;
        }

        public async Task<List<ISafetyMonitor>> GetSafetyMonitors() {
            var l = new List<ISafetyMonitor>();

            // Fetch the INDI driver that is supposed to be used from profile
            string driver = profileService.ActiveProfile.SafetyMonitorSettings.IndiDriver;

            // Query devices for this driver (safety monitors use the WEATHER_INTERFACE in INDI)
            foreach (var device in await INDIClient.Instance.GetDevices(DeviceInterface.WEATHER_INTERFACE, driver, "SafetyMonitor")) {
                IndiSafetyMonitor safetyMonitor = new(device, profileService);
                l.Add(safetyMonitor);
            }
            return l;
        }

        public async Task<List<IDome>> GetDomes() {
            var l = new List<IDome>();

            // Fetch the INDI driver that is supposed to be used from profile
            string driver = profileService.ActiveProfile.DomeSettings.IndiDriver;

            // Query devices for this driver
            foreach (var device in await INDIClient.Instance.GetDevices(DeviceInterface.DOME_INTERFACE, driver, "Dome")) {
                IndiDome dome = new(device, profileService);
                l.Add(dome);
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
