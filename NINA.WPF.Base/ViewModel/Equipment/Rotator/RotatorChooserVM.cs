#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Equipment.Equipment.MyRotator;
using NINA.Core.Utility;
using NINA.Profile.Interfaces;
using System;
using System.Collections.Generic;
using NINA.Core.Locale;
using NINA.Equipment.Utility;
using NINA.Equipment.Equipment;
using NINA.Equipment.Interfaces;
using System.Threading.Tasks;
using NINA.Equipment.Interfaces.ViewModel;
using Nitecrawler;
using Wanderer;

namespace NINA.WPF.Base.ViewModel.Equipment.Rotator {

    public class RotatorChooserVM : DeviceChooserVM<IRotator> {
        public RotatorChooserVM(IProfileService profileService,
                                IEquipmentProviders<IRotator> equipmentProviders) : base(profileService, equipmentProviders) {
        }

        public override async Task GetEquipment() {
            await lockObj.WaitAsync();
            try {
                var devices = new List<IDevice>();

                devices.Add(new DummyDevice(Loc.Instance["LblNoRotator"]));

                /* Nitecrawler rotators */
                try {
                    Logger.Trace("Adding Nitecrawler Rotators");
                    int[] ids = new int[NitecrawlerSDK.NC_MAX_NUM];
                    NitecrawlerSDK.ScanRotators(out var rotators, ids);
                    for (int i = 0; i < rotators; i++) {
                        if (NitecrawlerSDK.GetVersion(ids[i], out var version) == NitecrawlerSDK.NC_ERROR_TYPE.NC_SUCCESS) {
                            var rotator = new NitecrawlerRotator(ids[i], version.serial, profileService);
                            Logger.Info($"Adding Nitecrawler Rotator: {rotator.Name}");
                            devices.Add(rotator);
                        }
                    }
                } catch (Exception ex) {
                    Logger.Error(ex);
                }

                /* Wanderer rotators */
                try {
                    Logger.Trace("Adding Wanderer Rotators");
                    int[] ids = new int[WandererRotatorSDK.WR_MAX_NUM];
                    try {
                        WandererRotatorSDK.RotatorScan(out var rotators, ids);
                        for (int i = 0; i < rotators; i++) {
                            try {
                                if (WandererRotatorSDK.RotatorGetVersion(ids[i], out var version) == WandererRotatorSDK.WR_ERROR_TYPE.WR_SUCCESS) {
                                    var modelString = version.model != null ? System.Text.Encoding.ASCII.GetString(version.model).TrimEnd('\0') : "Unknown";
                                    var rotator = new WandererRotator(ids[i], modelString, profileService);
                                    Logger.Info($"Adding Wanderer Rotator: {rotator.Name}");
                                    devices.Add(rotator);
                                }
                            } catch (Exception versionEx) {
                                Logger.Error($"Failed to get version for WandererRotator {ids[i]}: {versionEx}");
                            }
                        }
                    } catch (Exception scanEx) {
                        Logger.Error($"Failed to scan for WandererRotators: {scanEx}");
                    }
                } catch (Exception ex) {
                    Logger.Error(ex);
                }

                /* Plugin Providers */
                foreach (var provider in await equipmentProviders.GetProviders()) {
                    try {
                        var pluginDevices = provider.GetEquipment();
                        Logger.Info($"Found {pluginDevices?.Count} {provider.Name} Rotators");
                        devices.AddRange(pluginDevices);
                    } catch (Exception ex) {
                        Logger.Error(ex);
                    }
                }

                /* INDI rotator */
                try {
                    var indiInteraction = new INDIInteraction(profileService);
                    var indiRotator = await indiInteraction.GetRotators();
                    devices.AddRange(indiRotator);
                    Logger.Info($"Found {indiRotator?.Count} INDI Rotators");
                } catch (Exception ex) {
                    Logger.Error(ex);
                }

                /* Alpaca */
                try {
                    var alpacaInteraction = new AlpacaInteraction(profileService);
                    var alpacaRotators = await alpacaInteraction.GetRotators(default);
                    foreach (IRotator r in alpacaRotators) {
                        devices.Add(r);
                    }
                    Logger.Info($"Found {alpacaRotators?.Count} Alpaca Rotators");
                } catch (Exception ex) {
                    Logger.Error(ex);
                }

                devices.Add(new ManualRotator(profileService));

                DetermineSelectedDevice(devices, profileService.ActiveProfile.RotatorSettings.Id, profileService.ActiveProfile.RotatorSettings.LastDeviceName);

            } finally {
                lockObj.Release();
            }
        }
    }
}
