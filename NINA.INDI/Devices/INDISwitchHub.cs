#region "copyright"

/*
    Copyright © 2025-2026 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.INDI.Enums;
using NINA.INDI.Interfaces;
using NINA.INDI.Protocol;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace NINA.INDI.Devices
{

    /// <summary>
    /// Generic INDI switch hub that maps an AUX-interface device (e.g. Pegasus
    /// UPB / UPB v2) to the NINA switch hub abstraction.
    ///
    /// After connecting, the device introspects all incoming INDI property
    /// definitions and builds a flat list of <see cref="INDISwitchDescriptor"/>
    /// objects from every writable switch- and number-vector property that is
    /// not an infrastructure property (connection management, driver config, …).
    ///
    /// OneOfMany switch vectors (radio-button style selectors such as tracking
    /// rates) are intentionally excluded because they cannot be treated as
    /// independent on/off channels.
    /// </summary>
    public class INDISwitchHub : INDIDevice, IINDISwitchHub
    {

        // ---------------------------------------------------------------------------
        // Properties to ignore entirely – these control INDI infrastructure, not the
        // device's user-facing channels.
        // ---------------------------------------------------------------------------
        private static readonly HashSet<string> SkippedProperties = new(System.StringComparer.OrdinalIgnoreCase) {
            "CONNECTION",
            "CONNECTION_MODE",
            "DEVICE_PORT",
            "DEVICE_BAUD_RATE",
            "DEVICE_AUTO_SEARCH",
            "DEVICE_ADDRESS",
            "CONFIG_PROCESS",
            "POLLING_PERIOD",
            "DEBUG",
            "SIMULATION",
            "DRIVER_INFO",
            "DRIVER_EXEC",
            "DEVICE_INFORMATION",
            "ACTIVE_DEVICES",
            "TELESCOPE_INFO",
            "SERVER_INFO"
        };

        // Groups that contain only infrastructure properties.
        private static readonly HashSet<string> SkippedGroups = new(System.StringComparer.OrdinalIgnoreCase) {
            "Connection",
            "Options"
        };

        private readonly List<INDISwitchDescriptor> _descriptors = new();
        private readonly object _descriptorsLock = new();

        public event System.Action<string> ValuesUpdated;

        public INDISwitchHub(INDIDeviceInfo device) : base(device) { }

        // No specific property required – the equipment layer adds its own wait.
        protected override string[] GetRequiredConnectionProperties() => System.Array.Empty<string>();

        // -------------------------------------------------------------------
        // IINDISwitchHub
        // -------------------------------------------------------------------

        public IReadOnlyList<INDISwitchDescriptor> GetDescriptors()
        {
            lock (_descriptorsLock)
            {
                return _descriptors.ToArray();
            }
        }

        public double GetValue(INDISwitchDescriptor descriptor)
        {
            if (descriptor.IsBoolSwitch)
            {
                return (GetSwitchPropertyValue(descriptor.PropertyName, descriptor.ElementName) ?? false) ? 1.0 : 0.0;
            }
            return GetNumberPropertyValue(descriptor.PropertyName, descriptor.ElementName) ?? 0.0;
        }

        public void SetBoolElement(INDISwitchDescriptor descriptor, bool value)
        {
            SetSwitchValue(descriptor.PropertyName, descriptor.ElementName, value);
        }

        public void SetNumberElement(INDISwitchDescriptor descriptor, double value)
        {
            SetNumberValue(descriptor.PropertyName, descriptor.ElementName, value);
        }

        // -------------------------------------------------------------------
        // Property update callbacks – build descriptors as properties arrive
        // -------------------------------------------------------------------

        public override void OnSwitchPropertyUpdated(INDISwitchProperty p)
        {
            base.OnSwitchPropertyUpdated(p);

            if (SkippedProperties.Contains(p.Name)) return;
            if (SkippedGroups.Contains(p.Group)) return;
            // OneOfMany = radio-button selector; not a discrete on/off channel.
            if (p.Rule == SwitchRule.OneOfMany) return;

            bool isWritable = p.Permission != PropertyPermission.ReadOnly;

            lock (_descriptorsLock)
            {
                // Refresh descriptors for this property (initial def or relabel).
                _descriptors.RemoveAll(d => d.PropertyName == p.Name && d.IsBoolSwitch);

                foreach (var sw in p.Switches)
                {
                    _descriptors.Add(new INDISwitchDescriptor
                    {
                        PropertyName = p.Name,
                        PropertyLabel = string.IsNullOrWhiteSpace(p.Label) ? p.Name : p.Label,
                        ElementName = sw.Name,
                        ElementLabel = string.IsNullOrWhiteSpace(sw.Label) ? sw.Name : sw.Label,
                        IsWritable = isWritable,
                        IsBoolSwitch = true,
                        Min = 0.0,
                        Max = 1.0,
                        Step = 1.0
                    });
                }
            }

            NotifyValuesUpdated(p.Name);
        }

        public override void OnNumberPropertyUpdated(INDINumberProperty p)
        {
            base.OnNumberPropertyUpdated(p);

            if (SkippedProperties.Contains(p.Name)) return;
            if (SkippedGroups.Contains(p.Group)) return;

            bool isWritable = p.Permission != PropertyPermission.ReadOnly;

            lock (_descriptorsLock)
            {
                _descriptors.RemoveAll(d => d.PropertyName == p.Name && !d.IsBoolSwitch);

                foreach (var num in p.Numbers)
                {
                    _descriptors.Add(new INDISwitchDescriptor
                    {
                        PropertyName = p.Name,
                        PropertyLabel = string.IsNullOrWhiteSpace(p.Label) ? p.Name : p.Label,
                        ElementName = num.Name,
                        ElementLabel = string.IsNullOrWhiteSpace(num.Label) ? num.Name : num.Label,
                        IsWritable = isWritable,
                        IsBoolSwitch = false,
                        Min = num.Min,
                        Max = num.Max,
                        Step = num.Step > 0 ? num.Step : 1.0
                    });
                }
            }

            NotifyValuesUpdated(p.Name);
        }

        /// <summary>
        /// Notifies the equipment layer that values for a property changed. Dispatched to
        /// the thread pool because the On*PropertyUpdated callbacks run on the receive
        /// thread while INDIClient's _lock is held — invoking handlers inline would execute
        /// arbitrary equipment-layer code inside that lock scope (deadlock-prone) and stall
        /// the receive loop. Handlers only re-poll current state, so ordering between
        /// successive notifications does not matter.
        /// </summary>
        private void NotifyValuesUpdated(string propertyName)
        {
            var handler = ValuesUpdated;
            if (handler != null)
            {
                Task.Run(() => handler(propertyName));
            }
        }

        public override void OnTextPropertyUpdated(INDITextProperty p)
        {
            base.OnTextPropertyUpdated(p);
        }

        public override void OnBlobPropertyUpdated(INDIBlobProperty p)
        {
            base.OnBlobPropertyUpdated(p);
        }

        // Action/Command* are inherited from INDIDevice — the redeclarations that used to
        // live here hid the base virtuals (CS0114) without changing behavior.
    }
}