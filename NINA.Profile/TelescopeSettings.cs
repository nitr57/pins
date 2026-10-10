#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Core.Enum;
using NINA.Profile.Interfaces;
using System;
using System.Runtime.Serialization;

namespace NINA.Profile {

    [Serializable()]
    [DataContract]
    public class TelescopeSettings : Settings, ITelescopeSettings {

        [OnDeserializing]
        public void OnDeserializing(StreamingContext context) {
            SetDefaultValues();
        }

        protected override void SetDefaultValues() {
            id = "No_Device";
            lastDeviceName = string.Empty;
            name = string.Empty;
            mountName = string.Empty;
            focalLength = double.NaN;
            focalRatio = double.NaN;
            snapPortStart = ":SNAP1,1#";
            snapPortStop = ":SNAP1,0#";
            settleTime = 5;
            noSync = false;
            timeSync = true;
            telescopeLocationSyncDirection = TelescopeLocationSyncDirection.PROMPT;
            indiConnectionMode = "CONNECTION_SERIAL";
            indiAutoSearch = true;
            indiAddress = "localhost";
            indiPort = "/dev/ttyUSB0";
            indiBaudRate = 9600;
            indiDriver = "None";
            indiMaxSlewRateDps = 4.0;
            serialPort = string.Empty;
            preferredPierSide = string.Empty;
        }

        private string id;

        [DataMember]
        public string Id {
            get => id;
            set {
                if (id != value) {
                    id = value;
                    RaisePropertyChanged();
                }
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

        private string name;

        [DataMember]
        public string Name {
            get => name;
            set {
                if (name != value) {
                    name = value;
                    RaisePropertyChanged();
                }
            }
        }

        private string mountName;

        [DataMember]
        public string MountName {
            get => mountName;
            set {
                if (mountName != value) {
                    mountName = value;
                    RaisePropertyChanged();
                }
            }
        }

        private double focalLength;

        [DataMember]
        public double FocalLength {
            get => focalLength;
            set {
                if (focalLength != value) {
                    focalLength = value;
                    RaisePropertyChanged();
                }
            }
        }

        private double focalRatio;

        [DataMember]
        public double FocalRatio {
            get => focalRatio;
            set {
                if (focalRatio != value) {
                    focalRatio = value;
                    RaisePropertyChanged();
                }
            }
        }

        private string snapPortStart;

        [DataMember]
        public string SnapPortStart {
            get => snapPortStart;
            set {
                if (snapPortStart != value) {
                    snapPortStart = value;
                    RaisePropertyChanged();
                }
            }
        }

        private string snapPortStop;

        [DataMember]
        public string SnapPortStop {
            get => snapPortStop;
            set {
                if (snapPortStop != value) {
                    snapPortStop = value;
                    RaisePropertyChanged();
                }
            }
        }

        private int settleTime;

        [DataMember]
        public int SettleTime {
            get => settleTime;
            set {
                if (settleTime != value) {
                    settleTime = value;
                    RaisePropertyChanged();
                }
            }
        }

        private bool noSync;

        [DataMember]
        public bool NoSync {
            get => noSync;
            set {
                if (noSync != value) {
                    noSync = value;
                    RaisePropertyChanged();
                }
            }
        }

        private bool primaryReversed;

        [DataMember]
        public bool PrimaryReversed {
            get => primaryReversed;
            set {
                if (primaryReversed != value) {
                    primaryReversed = value;
                    RaisePropertyChanged();
                }
            }
        }

        private bool secondaryReversed;

        [DataMember]
        public bool SecondaryReversed {
            get => secondaryReversed;
            set {
                if (secondaryReversed != value) {
                    secondaryReversed = value;
                    RaisePropertyChanged();
                }
            }
        }

        private TelescopeLocationSyncDirection telescopeLocationSyncDirection;
        [DataMember]
        public TelescopeLocationSyncDirection TelescopeLocationSyncDirection {
            get => telescopeLocationSyncDirection;
            set {
                if (telescopeLocationSyncDirection != value) {
                    telescopeLocationSyncDirection = value;
                    RaisePropertyChanged();
                }
            }
        }

        private bool timeSync;
        [DataMember]
        public bool TimeSync {
            get => timeSync;
            set {
                if (timeSync != value) {
                    timeSync = value;
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

        private double indiMaxSlewRateDps;
        [DataMember]
        public double IndiMaxSlewRateDps {
            get => indiMaxSlewRateDps;
            set {
                if (value <= 0) return;
                if (indiMaxSlewRateDps != value) {
                    indiMaxSlewRateDps = value;
                    RaisePropertyChanged();
                }
            }
        }

        private string serialPort;

        /// <summary>The serial port of a mount driven by pins directly (OnStepX), e.g. /dev/ttyUSB0.</summary>
        [DataMember]
        public string SerialPort {
            get => serialPort;
            set {
                if (serialPort != value) {
                    serialPort = value;
                    RaisePropertyChanged();
                }
            }
        }

        private string preferredPierSide;

        /// <summary>
        /// The pier side the mount prefers for gotos: "East", "West" or "Best" (stay on the current side as long as
        /// possible); empty leaves the mount's own setting. Mount drivers that support it set it at every connect
        /// (so far pins' native OnStepX driver, :SX96). With "Best" a goto for a meridian flip may stay on the same side
        /// until the meridian limit.
        /// </summary>
        [DataMember]
        public string PreferredPierSide {
            get => preferredPierSide;
            set {
                if (preferredPierSide != value) {
                    preferredPierSide = value;
                    RaisePropertyChanged();
                }
            }
        }
    }
}
