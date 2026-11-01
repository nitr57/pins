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
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NINA.Equipment.Equipment.MySwitch {

    /// <summary>
    /// Equipment-layer wrapper for an INDI AUX device (e.g. Pegasus UPB) that
    /// exposes power ports, USB ports, dew-heater duty cycles, and other
    /// writable channels as a standard NINA <see cref="ISwitchHub"/>.
    ///
    /// Switch collection is built once after connection by inspecting all writable
    /// INDI property elements that the driver has advertised. Channel ids come from
    /// the property and element names (<see cref="IndiSwitchChannelIds"/>).
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
                if (s.IndiPreConnectDelay >= 0) {
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
            var descriptors = device.GetDescriptors();
            var ids = IndiSwitchChannelIds.Assign(descriptors);
            for (var i = 0; i < descriptors.Count; i++) {
                var desc = descriptors[i];
                list.Add(desc.IsWritable
                    ? (ISwitch)new IndiWritableSwitchItem(ids[i], desc, device)
                    : new IndiReadSwitchItem(ids[i], desc, device));
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
    // Channel ids
    // -----------------------------------------------------------------------

    /// <summary>
    /// Derives each channel's <see cref="ISwitch.Id"/> from its INDI property and element name, so a
    /// channel keeps its id across sessions however the driver's properties arrive. A Set Switch Value
    /// instruction saves the hub's device id and this id as the channel's identity.
    /// </summary>
    internal static class IndiSwitchChannelIds {

        /// <summary>
        /// Lowest id. Ids used to be the position in the channel list (0, 1, 2, …); keeping new ids
        /// above them makes an instruction saved with an old id find no channel instead of another one.
        /// </summary>
        public const short MinId = 1000;

        private const int IdCount = short.MaxValue - MinId + 1;

        /// <summary>Returns the ids in the order of <paramref name="descriptors"/>.</summary>
        public static short[] Assign(IReadOnlyList<INDISwitchDescriptor> descriptors) {
            var ids = new short[descriptors.Count];
            var taken = new HashSet<short>();

            // Two names whose hashes collide are resolved in name order, so the result does not
            // depend on the order of the descriptors.
            var order = Enumerable.Range(0, descriptors.Count)
                .OrderBy(i => descriptors[i].PropertyName, StringComparer.Ordinal)
                .ThenBy(i => descriptors[i].ElementName, StringComparer.Ordinal)
                .ToArray();
            foreach (var i in order) {
                var id = PreferredId(descriptors[i].PropertyName, descriptors[i].ElementName);
                while (!taken.Add(id)) {
                    id = id == short.MaxValue ? MinId : (short)(id + 1);
                }

                ids[i] = id;
            }

            return ids;
        }

        /// <summary>
        /// FNV-1a (32 bit) over the UTF-8 bytes of "property\0element", mapped onto
        /// <see cref="MinId"/>..<see cref="short.MaxValue"/>. Fixed rather than
        /// <see cref="string.GetHashCode()"/>, which differs from process to process.
        /// </summary>
        public static short PreferredId(string propertyName, string elementName) {
            var hash = 2166136261u;
            foreach (var b in Encoding.UTF8.GetBytes(propertyName + "\0" + elementName)) {
                hash ^= b;
                hash *= 16777619u;
            }

            return (short)(MinId + hash % IdCount);
        }
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
