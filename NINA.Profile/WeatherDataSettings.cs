#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Profile.Interfaces;
using System;
using System.Runtime.Serialization;

namespace NINA.Profile {

    [Serializable()]
    [DataContract]
    public class WeatherDataSettings : Settings, IWeatherDataSettings, IHasDeviceHubSettings {

        [OnDeserializing]
        public void OnDeserializing(StreamingContext context) {
            SetDefaultValues();
        }

        protected override void SetDefaultValues() {
            HubSettings = new WeatherHubSettings();
            Id = "No_Device";
            LastDeviceName = string.Empty;
            OpenWeatherMapAPIKey = string.Empty;
            TheWeatherCompanyAPIKey = string.Empty;
            WeatherUndergroundAPIKey = string.Empty;
            WeatherUndergroundStation = string.Empty;
            indiConnectionMode = "CONNECTION_SERIAL";
            indiPort = "/dev/ttyUSB0";
            indiBaudRate = 9600;
            indiAutoSearch = true;
            indiAddress = "localhost";
            indiDriver = "None";
        }

        IDeviceHubSettings IHasDeviceHubSettings.HubSettings => HubSettings;

        private DeviceHubSettings hubSettings;

        [DataMember]
        public DeviceHubSettings HubSettings {
            get => hubSettings;
            set {
                if (ReferenceEquals(hubSettings, value)) {
                    return;
                }

                if (hubSettings != null) {
                    hubSettings.PropertyChanged -= HubSettingsChanged;
                }

                hubSettings = value ?? new WeatherHubSettings();
                hubSettings.PropertyChanged += HubSettingsChanged;
                RaisePropertyChanged();
            }
        }

        private void HubSettingsChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e) {
            RaisePropertyChanged(nameof(HubSettings));
        }

        private string id = string.Empty;

        [DataMember]
        public string Id {
            get => id;
            set {
                id = value;
                RaisePropertyChanged();
            }
        }

        private string lastDeviceName;

        [DataMember]
        public string LastDeviceName {
            get => lastDeviceName;
            set {
                if (lastDeviceName != value) {
                    lastDeviceName = value;
                    RaisePropertyChanged();
                }
            }
        }

        private string openWeatherMapAPIKey = string.Empty;

        [DataMember]
        public string OpenWeatherMapAPIKey {
            get => openWeatherMapAPIKey;
            set {
                openWeatherMapAPIKey = value;
                RaisePropertyChanged();
            }
        }

        private string theWeatherCompanyAPIKey = string.Empty;

        [DataMember]
        public string TheWeatherCompanyAPIKey {
            get => theWeatherCompanyAPIKey;
            set {
                theWeatherCompanyAPIKey = value;
                RaisePropertyChanged();
            }
        }

        private string weatherUndergroundAPIKey = string.Empty;

        [DataMember]
        public string WeatherUndergroundAPIKey {
            get => weatherUndergroundAPIKey;
            set {
                weatherUndergroundAPIKey = value;
                RaisePropertyChanged();
            }
        }

        private string weatherUndergroundStation = string.Empty;

        [DataMember]
        public string WeatherUndergroundStation {
            get => weatherUndergroundStation;
            set {
                weatherUndergroundStation = value;
                RaisePropertyChanged();
            }
        }

        private string indiDriver;
        [DataMember]
        public string IndiDriver {
            get => indiDriver;
            set {
                if (indiDriver != value) {
                    indiDriver = value;
                    RaisePropertyChanged();
                }
            }
        }

        private string indiConnectionMode;
        [DataMember]
        public string IndiConnectionMode {
            get => indiConnectionMode;
            set {
                if (indiConnectionMode != value) {
                    indiConnectionMode = value;
                    RaisePropertyChanged();
                }
            }
        }

        private string indiPort;
        [DataMember]
        public string IndiPort {
            get => indiPort;
            set {
                if (indiPort != value) {
                    indiPort = value;
                    RaisePropertyChanged();
                }
            }
        }

        private int indiBaudRate;
        [DataMember]
        public int IndiBaudRate {
            get => indiBaudRate;
            set {
                if (indiBaudRate != value) {
                    indiBaudRate = value;
                    RaisePropertyChanged();
                }
            }
        }

        private bool indiAutoSearch;
        [DataMember]
        public bool IndiAutoSearch {
            get => indiAutoSearch;
            set {
                if (indiAutoSearch != value) {
                    indiAutoSearch = value;
                    RaisePropertyChanged();
                }
            }
        }

        private string indiAddress;
        [DataMember]
        public string IndiAddress {
            get => indiAddress;
            set {
                if (indiAddress != value) {
                    indiAddress = value;
                    RaisePropertyChanged();
                }
            }
        }
    }
}
