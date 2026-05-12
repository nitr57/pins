#region "copyright"

/*
    Copyright © 2025-2026 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Core.Locale;
using NINA.Core.Utility;
using NINA.Equipment.Equipment;
using NINA.Equipment.Interfaces;
using NINA.INDI;
using NINA.INDI.Devices;
using NINA.INDI.Interfaces;
using NINA.Profile.Interfaces;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace NINA.Equipment.Equipment.MySwitch {

    /// <summary>
    /// Equipment-layer wrapper for an INDI AUX device (e.g. Pegasus UPB) that
    /// exposes power ports, USB ports, dew-heater duty cycles, and other
    /// writable channels as a standard NINA <see cref="ISwitchHub"/>.
    ///
    /// Switch collection is built once after connection by inspecting all writable
    /// INDI property elements that the driver has advertised.
    /// </summary>
    public class IndiSwitchHub : IndiDevice<IINDISwitchHub>, ISwitchHub, IDisposable {

        public IndiSwitchHub(INDIDeviceInfo info, IProfileService profileService = null) : base(info) {
            this.profileService = profileService;
            switches = new AsyncObservableCollection<ISwitch>();
        }

        private readonly IProfileService profileService;

        private ICollection<ISwitch> switches;
        public ICollection<ISwitch> Switches {
            get => switches;
            private set {
                switches = value;
                RaisePropertyChanged();
            }
        }

        protected override string ConnectionLostMessage => Loc.Instance["LblSwitchConnectionLost"];

        protected override IINDISwitchHub GetInstance() {
            return device ??= new INDISwitchHub(_device);
        }

        protected override Task PreConnect() {
            if (profileService != null) {
                var s = profileService.ActiveProfile.SwitchSettings;
                GetInstance().ConfigureConnectionProperties(
                    s.IndiConnectionMode,
                    s.IndiAutoSearch,
                    s.IndiAddress,
                    s.IndiPort,
                    s.IndiBaudRate
                );
                if (s.IndiPreConnectDelay > 0) {
                    GetInstance().ConfigurePreConnectDelay(TimeSpan.FromSeconds(s.IndiPreConnectDelay));
                }
            }
            return Task.CompletedTask;
        }

        protected override async Task PostConnect() {
            var postDelay = profileService?.ActiveProfile.SwitchSettings.IndiPostConnectDelay ?? 1;
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, postDelay)));
            BuildSwitchCollection();
            GetInstance().ValuesUpdated += OnIndiValuesUpdated;
        }

        protected override void PostDisconnect() {
            GetInstance().ValuesUpdated -= OnIndiValuesUpdated;
            Switches = new AsyncObservableCollection<ISwitch>();
        }

        private void OnIndiValuesUpdated(string propertyName) {
            foreach (var sw in switches) {
                if (sw is IndiReadSwitchItem r && r.PropertyName == propertyName) {
                    r.Poll();
                } else if (sw is IndiWritableSwitchItem w && w.PropertyName == propertyName) {
                    // SyncFromDevice syncs TargetValue from the live device state so the
                    // Vue toggle reflects external changes (e.g. from another INDI client).
                    w.SyncFromDevice();
                }
            }
        }

        private void BuildSwitchCollection() {
            var list = new AsyncObservableCollection<ISwitch>();
            short id = 0;
            foreach (var desc in device.GetDescriptors()) {
                list.Add(desc.IsWritable
                    ? (ISwitch)new IndiWritableSwitchItem(id++, desc, device)
                    : new IndiReadSwitchItem(id++, desc, device));
            }
            Switches = list;
            Logger.Info($"IndiSwitchHub '{Name}': discovered {list.Count} switch channel(s)");
        }

        #region Unsupported

        public IList<string> SupportedActions { get; } = new List<string>();
        public string Action(string actionName, string actionParameters) => throw new NotImplementedException();
        public void CommandBlind(string command, bool raw = false) => throw new NotImplementedException();
        public bool CommandBool(string command, bool raw = false) => throw new NotImplementedException();
        public string CommandString(string command, bool raw = false) => throw new NotImplementedException();

        #endregion
    }

    // -----------------------------------------------------------------------
    // Read-only switch item (INDI RO property element)
    // -----------------------------------------------------------------------

    internal sealed class IndiReadSwitchItem : BaseINPC, ISwitch {
        private readonly INDISwitchDescriptor _descriptor;
        private readonly IINDISwitchHub _hub;

        public IndiReadSwitchItem(short id, INDISwitchDescriptor descriptor, IINDISwitchHub hub) {
            Id = id;
            _descriptor = descriptor;
            _hub = hub;
            Name = string.IsNullOrWhiteSpace(descriptor.ElementLabel) ? descriptor.ElementName : descriptor.ElementLabel;
            Description = string.IsNullOrWhiteSpace(descriptor.PropertyLabel) ? descriptor.PropertyName : descriptor.PropertyLabel;
        }

        public short Id { get; }
        public string Name { get; }
        public string Description { get; }
        public string PropertyName => _descriptor.PropertyName;
        public double Value => _hub.GetValue(_descriptor);

        public bool Poll() {
            RaisePropertyChanged(nameof(Value));
            return true;
        }
    }

    // -----------------------------------------------------------------------
    // Writable switch item – boolean toggle or numeric control
    // -----------------------------------------------------------------------

    internal sealed class IndiWritableSwitchItem : BaseINPC, IWritableSwitch {
        private readonly INDISwitchDescriptor _descriptor;
        private readonly IINDISwitchHub _hub;

        public IndiWritableSwitchItem(short id, INDISwitchDescriptor descriptor, IINDISwitchHub hub) {
            Id = id;
            _descriptor = descriptor;
            _hub = hub;
            Name = string.IsNullOrWhiteSpace(descriptor.ElementLabel) ? descriptor.ElementName : descriptor.ElementLabel;
            Description = string.IsNullOrWhiteSpace(descriptor.PropertyLabel) ? descriptor.PropertyName : descriptor.PropertyLabel;
            PropertyName = descriptor.PropertyName;
            Maximum = descriptor.Max;
            Minimum = descriptor.Min;
            StepSize = descriptor.Step > 0 ? descriptor.Step : 1.0;
            targetValue = hub.GetValue(descriptor);
        }

        public short Id { get; }
        public string Name { get; }
        public string Description { get; }
        public string PropertyName { get; }
        public double Maximum { get; }
        public double Minimum { get; }
        public double StepSize { get; }

        public double Value => _hub.GetValue(_descriptor);

        private double targetValue;
        public double TargetValue {
            get => targetValue;
            set {
                if (targetValue != value) {
                    targetValue = value;
                    RaisePropertyChanged();
                }
            }
        }

        public bool Poll() {
            RaisePropertyChanged(nameof(Value));
            return true;
        }

        /// <summary>
        /// Called when the INDI server pushes an unsolicited state update (setSwitchVector).
        /// Syncs <see cref="TargetValue"/> to the actual device state so that the UI
        /// toggle reflects external changes without disrupting the SwitchVM set-value
        /// polling loop (which relies on Value != TargetValue to detect work-in-progress).
        /// </summary>
        public void SyncFromDevice() {
            var liveValue = _hub.GetValue(_descriptor);
            if (Math.Abs(targetValue - liveValue) > 0.001) {
                targetValue = liveValue;
                RaisePropertyChanged(nameof(TargetValue));
            }
            RaisePropertyChanged(nameof(Value));
        }

        public void SetValue() {
            if (_descriptor.IsBoolSwitch) {
                _hub.SetBoolElement(_descriptor, TargetValue >= 0.5);
            } else {
                _hub.SetNumberElement(_descriptor, TargetValue);
            }
        }
    }
}
