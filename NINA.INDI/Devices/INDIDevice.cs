#region "copyright"

/*
    Copyright © 2025 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Core.Utility;
using NINA.INDI.Enums;
using NINA.INDI.Interfaces;
using NINA.INDI.Protocol;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.INDI.Devices
{

    public class PropertyEventArgs : EventArgs
    {
        public INDIProperty Property { get; }

        public PropertyEventArgs(INDIProperty property)
        {
            Property = property;
        }
    }

    public class INDIDevice : IINDIDevice
    {

        private readonly INDIDeviceInfo _device;

        public INDIDevice(INDIDeviceInfo device)
        {
            _device = device;

            // Register device to receive property updates
            INDIClient.Instance.RegisterDevice(this);

            // Request fresh properties from the driver
            INDIClient.Instance.GetProperties(Id);

            // Wait for CONNECTION property (always required, according to INDI)
            string[] requiredProps = ["CONNECTION"];
            var propTimeout = DateTime.Now.AddSeconds(10);
            while (!HasProperties(requiredProps) && DateTime.Now < propTimeout)
            {
                CoreUtil.Wait(TimeSpan.FromMilliseconds(500)).Wait();
            }

            // Record whether the driver actually described itself. If CONNECTION never
            // arrived, the driver did not respond (crash, load failure, server gone) — this
            // is NOT a USB device, it's "INDI returned nothing", and must not be treated as
            // connectable-directly.
            _connectionPropertyFound = HasProperties(["CONNECTION"]);

            // Connection-plugin properties (DEVICE_PORT/DEVICE_ADDRESS/CONNECTION_MODE/...) are
            // defined by the driver in the SAME initProperties() burst as CONNECTION, but the
            // client may process them a few messages after CONNECTION. Settle briefly so the
            // snapshot below reliably reflects the driver's full property set rather than a
            // mid-burst race (otherwise a serial device could momentarily look port-less).
            if (_connectionPropertyFound)
            {
                CoreUtil.Wait(TimeSpan.FromMilliseconds(600)).Wait();
            }

            // Other connection properties
            _hasConnectionModeProperty = HasProperties(["CONNECTION_MODE"]);
            _hasDevicePortProperty = HasProperties(["DEVICE_PORT"]);
            _hasDeviceBaudRateProperty = HasProperties(["DEVICE_BAUD_RATE"]);
            _hasAutoSearchProperty = HasProperties(["DEVICE_AUTO_SEARCH"]);
            _hasDeviceAddressProperty = HasProperties(["DEVICE_ADDRESS"]);

            // Positive identification of a direct-USB / SDK device (ASI camera, ZWO EAF,
            // ZWO CAA, ...). INDI has no CONNECTION_USB property — USB drivers use libusb
            // directly (INDI::USBDevice) and expose ONLY the base CONNECTION property. So a
            // device is direct-USB iff it described itself (CONNECTION present) AND exposes
            // none of the transport-configuration properties. This is deliberately gated on
            // _connectionPropertyFound so a non-responding driver is never misclassified.
            _isDirectUsbDevice = _connectionPropertyFound
                && !_hasConnectionModeProperty
                && !_hasDevicePortProperty
                && !_hasDeviceAddressProperty
                && !_hasAutoSearchProperty;
        }

        private readonly bool _connectionPropertyFound;
        private readonly bool _isDirectUsbDevice;
        private readonly bool _hasConnectionModeProperty;
        private readonly bool _hasDevicePortProperty;
        private readonly bool _hasDeviceBaudRateProperty;
        private readonly bool _hasAutoSearchProperty;
        private readonly bool _hasDeviceAddressProperty;

        private bool _connected;
        private bool _connectionAttemptFailed;  // Track if the last connection attempt failed
        private volatile Task _disconnectTask;  // In-flight graceful disconnect (see Disconnect/Dispose)

        public bool Connected
        {
            get => _connected;
            set
            {
                if (_connected && !value)
                {
                    // Transitioning from connected to disconnected
                    Disconnect();
                }
                _connected = value;
            }
        }

        public string Category => "INDI Device";
        public string Id => _device.Id;
        public string DeviceName => _device.Name;
        public string Name => _device.Name;
        public string DisplayName => DeviceName;
        public string Description => $"INDI Device: {DeviceName}";
        public string DriverInfo => _device?.Driver ?? "INDI Driver";
        public string DriverVersion => _device?.Version ?? "1.0";

        private readonly Dictionary<string, INDIProperty> _properties = new();

        public void AddProperty(INDIProperty property)
        {
            lock (_properties)
            {
                _properties[property.Name] = property;
            }
        }

        public void RemoveProperty(string propertyName)
        {
            lock (_properties)
            {
                _properties.Remove(propertyName);
            }
        }

        public void RemoveAllProperties()
        {
            lock (_properties)
            {
                _properties.Clear();
            }
        }

        public INDIProperty GetProperty(string propertyName)
        {
            lock (_properties)
            {
                _properties.TryGetValue(propertyName, out var property);
                return property;
            }
        }

        public INDINumberProperty GetNumberProperty(string propertyName)
        {
            return GetProperty(propertyName) as INDINumberProperty;
        }

        public INDISwitchProperty GetSwitchProperty(string propertyName)
        {
            return GetProperty(propertyName) as INDISwitchProperty;
        }

        public INDITextProperty GetTextProperty(string propertyName)
        {
            return GetProperty(propertyName) as INDITextProperty;
        }

        public double? GetNumberPropertyValue(string propertyName, string elementName)
        {
            var prop = GetNumberProperty(propertyName);
            return prop?.Numbers.FirstOrDefault(n => n.Name == elementName)?.Value;
        }

        public bool? GetSwitchPropertyValue(string propertyName, string elementName)
        {
            var prop = GetSwitchProperty(propertyName);
            return prop?.Switches.FirstOrDefault(s => s.Name == elementName)?.Value;
        }

        public string GetTextPropertyValue(string propertyName, string elementName)
        {
            var prop = GetTextProperty(propertyName);
            return prop?.Texts.FirstOrDefault(t => t.Name == elementName)?.Value;
        }

        public void SetNumberValue(string propertyName, string elementName, double value)
        {
            var prop = GetNumberProperty(propertyName) ?? throw new ArgumentException($"Number property '{propertyName}' not found");

            var number = prop.Numbers.FirstOrDefault(n => n.Name == elementName);
            if (number == null) return;

            number.Value = value;
            INDIClient.Instance.SendProperty(prop);
        }

        public void SetNumberValues(string propertyName, params (string elementName, double value)[] values)
        {
            var prop = GetNumberProperty(propertyName) ?? throw new ArgumentException($"Number property '{propertyName}' not found");

            foreach (var (elementName, value) in values)
            {
                var number = prop.Numbers.FirstOrDefault(n => n.Name == elementName);
                if (number != null)
                {
                    number.Value = value;
                }
            }

            INDIClient.Instance.SendProperty(prop);
        }

        public async Task<bool> SetNumberValuesAsync(string propertyName, TimeSpan timeout, params (string elementName, double value)[] values)
        {
            try
            {
                // Fail fast when NONE of the requested elements exist: SetNumberValues would
                // then send the vector unchanged — a silent no-op the driver happily acks,
                // which would resolve the wait below as a false success.
                var existing = GetNumberProperty(propertyName);
                if (existing != null && !values.Any(v => existing.Numbers.Any(n => n.Name == v.elementName)))
                {
                    Logger.Warning($"[{DeviceName}] SetNumberValuesAsync: none of the requested elements [{string.Join(", ", values.Select(v => v.elementName))}] exist on '{propertyName}' (has: {string.Join(", ", existing.Numbers.Select(n => n.Name))}) — failing fast");
                    return false;
                }

                // Create a unique operation ID for this async set
                var operationId = $"{propertyName}_{Guid.NewGuid()}";

                // Capture pre-send state before registering the TCS so the timestamp comparison
                // in OnNumberPropertyUpdated can tell a stale Alert from a real rejection.
                var preSendProp = GetProperty(propertyName);
                var preSendTimestamp = preSendProp?.Timestamp ?? string.Empty;
                Logger.Info($"SetNumberValuesAsync ({propertyName}) sending [{string.Join(", ", values.Select(v => $"{v.elementName}={v.value}"))}]; " +
                            $"pre-send state={preSendProp?.State.ToString() ?? "null"}, timestamp={(string.IsNullOrEmpty(preSendTimestamp) ? "n/a" : preSendTimestamp)}");

                // Create and register the TaskCompletionSource.
                // Numbers keep the timestamp-only behaviour (no value predicate): a number
                // reaching its target value does not necessarily mean the operation is done
                // (e.g. a slew passes through the target), so we leave the predicate null.
                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_asyncOperationsLock)
                {
                    _pendingAsyncOperations[operationId] = (tcs, propertyName, preSendTimestamp, null);
                }

                try
                {
                    // Execute the actual set operation
                    SetNumberValues(propertyName, values);

                    // Wait for server acknowledgement (property state changes to Busy) with timeout
                    var timeoutTask = Task.Delay(timeout);
                    var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);

                    if (completedTask == timeoutTask)
                    {
                        Logger.Error($"SetNumberValuesAsync ({propertyName}) - server did not acknowledge within timeout");
                        return false;
                    }

                    var result = await tcs.Task;
                    if (!result)
                    {
                        Logger.Error($"SetNumberValuesAsync ({propertyName}) - server rejected operation (Alert state)");
                        return false;
                    }

                    // Server acknowledged the operation (state changed to Busy)
                    Logger.Debug($"SetNumberValuesAsync ({propertyName}) - server acknowledged, operation proceeding");
                    return true;
                }
                finally
                {
                    // Clean up the pending operation
                    lock (_asyncOperationsLock)
                    {
                        _pendingAsyncOperations.Remove(operationId);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Logger.Warning($"SetNumberValuesAsync ({propertyName}) was cancelled");
                return false;
            }
            catch (Exception ex)
            {
                Logger.Error($"SetNumberValuesAsync failed: {ex.Message}");
                return false;
            }
        }

        public async Task<bool> SetSwitchValueAsync(string propertyName, string elementName, bool value, TimeSpan timeout)
        {
            try
            {
                // Fail fast when the element does not exist on the vector: SetSwitchValue
                // would skip the send (see its guard), so no state transition could ever
                // resolve the pending operation — the wait would burn the full timeout, and
                // an unrelated re-broadcast of the property could even resolve it as a false
                // success. Reachable with profile-driven element names (CONNECTION_MODE,
                // DEVICE_BAUD_RATE). A missing PROPERTY still goes through the send, whose
                // ArgumentException the catch below turns into a fast false.
                var existing = GetSwitchProperty(propertyName);
                if (existing != null && existing.Switches.All(s => s.Name != elementName))
                {
                    Logger.Warning($"[{DeviceName}] SetSwitchValueAsync: element '{elementName}' does not exist on '{propertyName}' (has: {string.Join(", ", existing.Switches.Select(s => s.Name))}) — failing fast");
                    return false;
                }

                // Create a unique operation ID for this async set
                var operationId = $"{propertyName}_{Guid.NewGuid()}";

                var preSendTimestamp = GetProperty(propertyName)?.Timestamp ?? string.Empty;

                // Predicate to detect that the requested value was actually applied. INDI
                // timestamps only have 1-second resolution, so a fast driver can answer within
                // the same second the property was defined — which the timestamp-only stale
                // guard would mistake for a re-broadcast. If the desired value is present we
                // accept the Ok regardless of the (unchanged) timestamp.
                Func<INDIProperty, bool> desiredReached = prop =>
                {
                    if (prop is INDISwitchProperty sw)
                    {
                        var s = sw.Switches.FirstOrDefault(x => x.Name == elementName);
                        return s != null && s.Value == value;
                    }
                    return false;
                };

                // Create and register the TaskCompletionSource
                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_asyncOperationsLock)
                {
                    _pendingAsyncOperations[operationId] = (tcs, propertyName, preSendTimestamp, desiredReached);
                }

                try
                {
                    // Execute the actual set operation
                    SetSwitchValue(propertyName, elementName, value);

                    // Wait for server acknowledgement (property state changes to Busy) with timeout
                    var timeoutTask = Task.Delay(timeout);
                    var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);

                    if (completedTask == timeoutTask)
                    {
                        Logger.Error($"SetSwitchValueAsync ({propertyName}) - server did not acknowledge within timeout");
                        return false;
                    }

                    var result = await tcs.Task;
                    if (!result)
                    {
                        Logger.Error($"SetSwitchValueAsync ({propertyName}) - server rejected operation (Alert state)");
                        return false;
                    }

                    // Server acknowledged the operation (state changed to Busy)
                    Logger.Debug($"SetSwitchValueAsync ({propertyName}) - server acknowledged, operation proceeding");
                    return true;
                }
                finally
                {
                    // Clean up the pending operation
                    lock (_asyncOperationsLock)
                    {
                        _pendingAsyncOperations.Remove(operationId);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Logger.Warning($"SetSwitchValueAsync ({propertyName}) was cancelled");
                return false;
            }
            catch (Exception ex)
            {
                Logger.Error($"SetSwitchValueAsync failed: {ex.Message}");
                return false;
            }
        }

        public void SetSwitchValue(string propertyName, string elementName, bool value)
        {
            var prop = GetSwitchProperty(propertyName) ?? throw new ArgumentException($"Switch property '{propertyName}' not found");

            // Mirror SetNumberValue/SetTextValue: skip when the element does not exist.
            // Proceeding would be worse for switches — the OneOfMany branch below switches
            // everything off first, so a bad element name (e.g. a profile baud rate that
            // matches no DEVICE_BAUD_RATE element) would send an illegal all-off vector.
            if (prop.Switches.All(s => s.Name != elementName))
            {
                Logger.Warning($"[{DeviceName}] Switch element '{elementName}' not found on '{propertyName}' — not sending");
                return;
            }

            // Handle switch rules
            if (prop.Rule == SwitchRule.OneOfMany)
            {
                // For OneOfMany, only allow setting one switch to true at a time
                // First, turn off all switches
                foreach (var sw in prop.Switches)
                {
                    sw.Value = false;
                }
                // Then turn on only the requested one (if value is true)
                if (value)
                {
                    var targetSw = prop.Switches.FirstOrDefault(s => s.Name == elementName);
                    if (targetSw != null)
                    {
                        targetSw.Value = true;
                    }
                }
            }
            else if (prop.Rule == SwitchRule.AtMostOne)
            {
                if (value)
                {
                    // Turn off all other switches
                    foreach (var sw in prop.Switches)
                    {
                        sw.Value = sw.Name == elementName;
                    }
                }
                else
                {
                    // Just turn off this switch, leave others as is
                    var sw = prop.Switches.FirstOrDefault(s => s.Name == elementName);
                    if (sw != null)
                    {
                        sw.Value = false;
                    }
                }
            }
            else // AnyOfMany
            {
                var sw = prop.Switches.FirstOrDefault(s => s.Name == elementName);
                if (sw != null)
                {
                    sw.Value = value;
                }
            }

            INDIClient.Instance.SendProperty(prop);
        }

        public void SetSwitchProperty(string propertyName, Dictionary<string, bool> values)
        {
            var prop = GetSwitchProperty(propertyName) ?? throw new ArgumentException($"Switch property '{propertyName}' not found");

            // Validate based on switch rule — against the elements that actually exist on
            // this vector. Counting the requested dictionary alone would let a 'true' aimed
            // at a nonexistent element (e.g. TRACK_CUSTOM on a mount that doesn't expose it)
            // pass validation, silently drop out in the apply loop, and send an illegal
            // all-off OneOfMany update.
            var applicable = prop.Switches.Where(sw => values.ContainsKey(sw.Name)).ToList();
            var trueCount = applicable.Count(sw => values[sw.Name]);

            if (prop.Rule == SwitchRule.OneOfMany)
            {
                // Must have exactly one switch set to true
                if (trueCount != 1)
                {
                    throw new ArgumentException($"OneOfMany rule requires exactly one existing switch to be true, got {trueCount} (vector has: {string.Join(", ", prop.Switches.Select(s => s.Name))})");
                }
            }
            else if (prop.Rule == SwitchRule.AtMostOne)
            {
                // Can have at most one switch set to true
                if (trueCount > 1)
                {
                    throw new ArgumentException($"AtMostOne rule allows at most one switch to be true, got {trueCount}");
                }
            }
            // AnyOfMany has no restrictions

            if ((prop.Rule == SwitchRule.OneOfMany || prop.Rule == SwitchRule.AtMostOne) && trueCount == 1)
            {
                // Radio behavior: the single 'on' element defines the whole vector. Switch
                // everything else off, including elements the caller didn't mention — the
                // caller may not know the full element set, and leaving another element on
                // would violate the rule.
                var chosen = applicable.First(sw => values[sw.Name]).Name;
                foreach (var sw in prop.Switches)
                {
                    sw.Value = sw.Name == chosen;
                }
            }
            else
            {
                // Apply the values
                foreach (var sw in prop.Switches)
                {
                    if (values.TryGetValue(sw.Name, out bool value))
                    {
                        sw.Value = value;
                    }
                }
            }

            INDIClient.Instance.SendProperty(prop);
        }

        public void SetTextValue(string propertyName, string elementName, string value)
        {
            var prop = GetTextProperty(propertyName) ?? throw new ArgumentException($"Text property '{propertyName}' not found");

            var text = prop.Texts.FirstOrDefault(t => t.Name == elementName);
            if (text == null) return;

            text.Value = value;
            INDIClient.Instance.SendProperty(prop);
        }

        // Sends multiple elements of the same text vector in one update.
        // Use when the driver acts on all elements atomically (e.g. TIME_UTC: UTC + OFFSET).
        public void SetTextValues(string propertyName, params (string elementName, string value)[] values)
        {
            var prop = GetTextProperty(propertyName) ?? throw new ArgumentException($"Text property '{propertyName}' not found");

            foreach (var (elementName, value) in values)
            {
                var text = prop.Texts.FirstOrDefault(t => t.Name == elementName);
                if (text != null)
                    text.Value = value;
            }

            INDIClient.Instance.SendProperty(prop);
        }

        public async Task<bool> SetTextValueAsync(string propertyName, string elementName, string value, TimeSpan timeout)
        {
            try
            {
                // Fail fast when the element does not exist — same rationale as
                // SetSwitchValueAsync: SetTextValue would silently skip the send and the
                // wait below could never be resolved by a real response.
                var existing = GetTextProperty(propertyName);
                if (existing != null && existing.Texts.All(t => t.Name != elementName))
                {
                    Logger.Warning($"[{DeviceName}] SetTextValueAsync: element '{elementName}' does not exist on '{propertyName}' (has: {string.Join(", ", existing.Texts.Select(t => t.Name))}) — failing fast");
                    return false;
                }

                // Create a unique operation ID for this async set
                var operationId = $"{propertyName}_{Guid.NewGuid()}";

                var preSendTimestamp = GetProperty(propertyName)?.Timestamp ?? string.Empty;

                // Predicate to detect that the requested value was actually applied (see the
                // note in SetSwitchValueAsync about the 1-second INDI timestamp resolution).
                Func<INDIProperty, bool> desiredReached = prop =>
                {
                    if (prop is INDITextProperty tp)
                    {
                        var t = tp.Texts.FirstOrDefault(x => x.Name == elementName);
                        return t != null && t.Value == value;
                    }
                    return false;
                };

                // Create and register the TaskCompletionSource
                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_asyncOperationsLock)
                {
                    _pendingAsyncOperations[operationId] = (tcs, propertyName, preSendTimestamp, desiredReached);
                }

                try
                {
                    // Execute the actual set operation
                    SetTextValue(propertyName, elementName, value);

                    // Wait for server acknowledgement (property state changes to Busy) with timeout
                    var timeoutTask = Task.Delay(timeout);
                    var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);

                    if (completedTask == timeoutTask)
                    {
                        Logger.Error($"SetTextValueAsync ({propertyName}) - server did not acknowledge within timeout");
                        return false;
                    }

                    var result = await tcs.Task;
                    if (!result)
                    {
                        Logger.Error($"SetTextValueAsync ({propertyName}) - server rejected operation (Alert state)");
                        return false;
                    }

                    // Server acknowledged the operation (state changed to Busy)
                    Logger.Debug($"SetTextValueAsync ({propertyName}) - server acknowledged, operation proceeding");
                    return true;
                }
                finally
                {
                    // Clean up the pending operation
                    lock (_asyncOperationsLock)
                    {
                        _pendingAsyncOperations.Remove(operationId);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Logger.Warning($"SetTextValueAsync ({propertyName}) was cancelled");
                return false;
            }
            catch (Exception ex)
            {
                Logger.Error($"SetTextValueAsync failed: {ex.Message}");
                return false;
            }
        }


        // For tracking multiple concurrent SetXxxAsync operations.
        // The tuple carries the TCS, the property the operation was issued against (kept as its
        // own field — INDI property names are prefixes of each other, e.g. CCD_EXPOSURE vs.
        // CCD_EXPOSURE_ABORT or CONNECTION vs. CONNECTION_MODE, so this must never be derived by
        // string-matching the dictionary key), the pre-send property timestamp (so that a stale
        // Alert/Ok already present before we sent can be distinguished from a real response), and
        // an optional DesiredReached predicate. The predicate lets us accept a same-second "Ok"
        // (INDI timestamps have only 1-second resolution) when the property actually reflects
        // the value we requested, instead of discarding it as a stale re-broadcast.
        private readonly Dictionary<string, (TaskCompletionSource<bool> Tcs, string PropertyName, string PreSendTimestamp, Func<INDIProperty, bool> DesiredReached)> _pendingAsyncOperations = new();
        private readonly object _asyncOperationsLock = new();

        private bool HasPendingOperationFor(string propertyName)
        {
            lock (_asyncOperationsLock)
            {
                return _pendingAsyncOperations.Values.Any(v => v.PropertyName == propertyName);
            }
        }

        public Task<bool> Connect(CancellationToken ct)
        {
            return Task.Run(async () =>
            {
                if (Connected)
                {
                    Logger.Warning($"Device '{DeviceName}' is already connected");
                    return true;
                }

                Logger.Info($"Connecting to INDI device: {DeviceName}");

                // Call hook to configure connection properties before connecting
                if (!await OnPreConnect())
                {
                    Logger.Error($"OnPreConnect failed for {DeviceName}");
                    return false;
                }

                // Apply optional pre-connect delay (mirrors the Ekos connection-profile setting).
                // Useful for devices like the SV241 Pro whose ESP32 needs time to finish booting
                // after the serial port is opened before the CONNECT command is sent.
                if (_preConnectDelay > TimeSpan.Zero)
                {
                    Logger.Info($"[{DeviceName}] Waiting {_preConnectDelay.TotalSeconds}s pre-connect delay");
                    await Task.Delay(_preConnectDelay, ct);
                }

                // If the INDI driver is already connected at the server level (e.g. a shared
                // driver whose other interface was connected first), skip the redundant CONNECT
                // command.  Sending CONNECT to an already-connected INDI device is well-defined
                // but many drivers simply ignore it and never send an ack, which would cause the
                // 30-second SetSwitchValueAsync timeout to fire.
                // NOTE: use only the CONNECT switch *value* — the property State can be Idle in
                // a defSwitchVector even when the device is actually connected, so do NOT gate
                // on State == Ok here.
                var connPropBefore = GetSwitchProperty("CONNECTION");
                var alreadyConnectedAtIndi =
                    connPropBefore?.Switches.FirstOrDefault(s => s.Name == "CONNECT")?.Value == true;

                bool success;
                if (alreadyConnectedAtIndi)
                {
                    Logger.Info($"[{DeviceName}] INDI driver already connected at server level — skipping CONNECT command");
                    success = true;
                }
                else
                {
                    success = await SetSwitchValueAsync("CONNECTION", "CONNECT", true, TimeSpan.FromSeconds(30));
                }

                if (success)
                {
                    Logger.Info($"Connected to INDI device: {DeviceName}");
                    _connectionAttemptFailed = false;  // Clear failure flag on successful connection

                    // Wait for initial property definitions to arrive from the driver
                    var requiredProps = GetRequiredConnectionProperties();
                    if (requiredProps != null && requiredProps.Length > 0)
                    {
                        Logger.Debug($"Waiting for required properties: {string.Join(", ", requiredProps)}");

                        // Poll for properties with timeout
                        var propStopwatch = System.Diagnostics.Stopwatch.StartNew();
                        while (!HasProperties(requiredProps) && propStopwatch.Elapsed < TimeSpan.FromSeconds(20) && !ct.IsCancellationRequested)
                        {
                            await CoreUtil.Wait(TimeSpan.FromMilliseconds(200), ct);
                        }
                        if (!HasProperties(requiredProps))
                        {
                            Logger.Warning($"[{DeviceName}] Required properties did not arrive within 20s: {string.Join(", ", requiredProps)}");
                        }
                    }

                    // The CONNECT ack above resolves on the driver's Busy state, which only
                    // means the driver STARTED connecting. If the attempt then fails
                    // (Busy → Alert, typical for TCP mounts whose socket connect times out),
                    // the Alert arrives after the pending operation is gone, so nothing above
                    // notices — the driver resets CONNECT to Off in that case. Re-read the
                    // switch as the final verdict instead of trusting the ack. (Limitation:
                    // if the required-props wait was skipped or instant, a slow Busy → Alert
                    // failure can still slip through, because our own send optimistically set
                    // CONNECT=On in the cache.)
                    if (!alreadyConnectedAtIndi)
                    {
                        var connPropAfter = GetSwitchProperty("CONNECTION");
                        var connectedNow = connPropAfter?.Switches.FirstOrDefault(s => s.Name == "CONNECT")?.Value == true;
                        if (!connectedNow)
                        {
                            Logger.Error($"[{DeviceName}] Driver acknowledged CONNECT but the connection did not complete (CONNECT=Off, state={connPropAfter?.State.ToString() ?? "unknown"})");
                            _connectionAttemptFailed = true;
                            success = false;
                        }
                    }
                }
                else
                {
                    Logger.Error($"Connecting to {DeviceName} failed");
                }

                Logger.Info($"[{DeviceName}] Setting Connected to {success}");
                Connected = success;

                return success;
            });
        }

        public async Task<bool> DisconnectAsync()
        {
            Logger.Info($"Disconnecting from INDI device: {DeviceName}");

            // Check if INDI client is still connected to server
            if (!INDIClient.Instance.IsConnected)
            {
                Logger.Info($"INDI server already disconnected, skipping graceful disconnect for {DeviceName}");
                _connected = false;
                return true;
            }

            // If the last connection attempt failed, skip DISCONNECT (device never actually connected)
            if (_connectionAttemptFailed)
            {
                Logger.Info($"Device '{DeviceName}' connection failed previously, skipping graceful disconnect");
                _connected = false;
                _connectionAttemptFailed = false;  // Reset flag
                return true;
            }

            // Check if device is actually connected before trying to disconnect
            var connProp = GetSwitchProperty("CONNECTION");
            if (connProp != null)
            {
                var connectSwitch = connProp.Switches.FirstOrDefault(s => s.Name == "CONNECT");
                if (connectSwitch != null && !connectSwitch.Value)
                {
                    Logger.Info($"Device '{DeviceName}' is not connected to server (CONNECT=false), skipping graceful disconnect");
                    _connected = false;
                    return true;
                }
            }

            // Send DISCONNECT command using async mechanism
            bool success = await SetSwitchValueAsync("CONNECTION", "DISCONNECT", true, TimeSpan.FromSeconds(60));

            if (success)
            {
                Logger.Info($"Disconnected from INDI device: {DeviceName}");
            }
            else
            {
                // If the driver was unloaded while we were waiting (e.g. the user switched to a
                // different INDI driver), the INDI server will never ack the DISCONNECT command.
                // Skip the failure and treat the device as disconnected.
                if (!INDIClient.Instance.IsDeviceKnown(Id))
                {
                    Logger.Info($"INDI device '{DeviceName}' driver was unloaded during disconnect — treating as disconnected");
                    success = true;
                }
                else
                {
                    Logger.Warning($"Disconnecting from {DeviceName} timed out or failed");
                }
            }

            _connected = false;
            return success;
        }

        public void Disconnect()
        {
            // Use async variant as fire-and-forget for Dispose compatibility. Keep a
            // handle on the task so Dispose can defer unregistration until the server
            // acknowledged the DISCONNECT (see Dispose).
            _disconnectTask = Task.Run(async () =>
            {
                try
                {
                    await DisconnectAsync();
                }
                catch (Exception ex)
                {
                    Logger.Error($"Disconnect failed: {ex.Message}");
                }
            });
        }

        public void Dispose()
        {
            if (Connected)
            {
                Disconnect();
            }

            // Close the raw LX200 TCP socket, if one was ever opened — otherwise it leaks
            // for the lifetime of the process (the mount side will eventually time it out,
            // but nothing on our side ever does).
            lock (_lx200Lock)
            {
                DisposeLx200Connection();
            }

            // Unregister device from client. If a graceful disconnect is still in flight,
            // defer unregistration until it finishes — unregistering immediately would cut
            // off property-update routing to this instance, so the CONNECTION ack could
            // never resolve the pending operation and would always hit the 60s timeout.
            var disconnectTask = _disconnectTask;
            if (disconnectTask != null && !disconnectTask.IsCompleted)
            {
                _ = disconnectTask.ContinueWith(_ => INDIClient.Instance.UnregisterDevice(this));
            }
            else
            {
                INDIClient.Instance.UnregisterDevice(this);
            }
        }

        /// <summary>
        /// Override this to specify which properties must be received before Connect() completes.
        /// Return null/empty to skip waiting (uses fixed delay fallback).
        /// </summary>
        protected virtual string[] GetRequiredConnectionProperties()
        {
            return null;
        }

        /// <summary>
        /// Override this to configure device properties after driver load but before CONNECT
        /// </summary>
        protected virtual async Task<bool> OnPreConnect()
        {
            // If the INDI driver is already connected (shared driver, other interface connected
            // first), skip all pre-connect property writes.  Attempting to change CONNECTION_MODE,
            // DEVICE_PORT or DEVICE_BAUD_RATE while connected causes many drivers to silently
            // ignore the command and never send an ack, which would result in up to 30 s of
            // cumulative timeouts before the CONNECT command is even sent.
            var connPropCurrent = GetSwitchProperty("CONNECTION");
            bool alreadyConnected = connPropCurrent?.Switches.FirstOrDefault(s => s.Name == "CONNECT")?.Value == true;
            if (alreadyConnected)
            {
                Logger.Info($"[{DeviceName}] OnPreConnect: INDI driver already connected — skipping connection property configuration");
                return true;
            }

            // Check if we have a valid connection mode setting
            bool HasConnectionMode = _hasConnectionModeProperty && !string.IsNullOrEmpty(_connectionMode);
            bool HasAddress = !string.IsNullOrEmpty(_address);
            bool HasPort = !string.IsNullOrEmpty(_port);
            bool IsAutoMode = _autoSearch && _hasAutoSearchProperty;

            // Determine actual connection mode
            bool IsUsingSerialMode = HasConnectionMode &&
                !_connectionMode.Equals("CONNECTION_TCP") &&
                !_connectionMode.Equals("CONNECTION_HTTP");
            bool IsUsingHttpMode = HasConnectionMode && _connectionMode.Equals("CONNECTION_HTTP");

            Logger.Info($"[{DeviceName}] Connection config: HasConnectionMode={HasConnectionMode}, HasAddress={HasAddress}, HasPort={HasPort}, IsAutoMode={IsAutoMode}, IsUsingSerialMode={IsUsingSerialMode}, IsUsingHttpMode={IsUsingHttpMode}");

            // Direct-USB / SDK device: the driver described itself (CONNECTION present) but
            // exposes no transport-configuration property at all. There is nothing to set up —
            // it connects via its own USB enumeration. Connect directly and ignore any stale
            // address/port left in the profile (those properties don't exist on the device, so
            // writing them would fail OnPreConnect). This is the same path USB cameras take.
            // Note: gated on a positive _connectionPropertyFound, so a non-responding driver is
            // NOT silently treated as connectable.
            if (_isDirectUsbDevice)
            {
                Logger.Info($"[{DeviceName}] Direct-USB/SDK device (CONNECTION present, no transport properties) - connecting directly");
                return true;
            }

            // If no connection configuration is available or configured, skip pre-connect (e.g., direct USB devices)
            if (!HasConnectionMode && !HasAddress && !HasPort && !IsAutoMode)
            {
                Logger.Info($"[{DeviceName}] No connection configuration needed - device will connect directly");
                return true;
            }

            // Validate configuration if we're trying to configure something
            if (HasConnectionMode || HasPort || HasAddress || IsAutoMode)
            {
                if (!IsAutoMode && !HasPort && !HasConnectionMode)
                {
                    Logger.Error($"[{DeviceName}] OnPreConnect validation failed: No auto-search and no port specified");
                    return false;
                }

                if (!IsUsingSerialMode && !IsUsingHttpMode && HasConnectionMode && (!HasAddress || !HasPort))
                {
                    Logger.Error($"[{DeviceName}] OnPreConnect validation failed: TCP mode requires both address ({HasAddress}) and port ({HasPort})");
                    return false;
                }

                if (IsUsingHttpMode && !HasAddress)
                {
                    Logger.Error($"[{DeviceName}] OnPreConnect validation failed: HTTP mode requires an address");
                    return false;
                }
            }

            // Apply connection mode
            if (HasConnectionMode)
            {
                Logger.Info($"[{DeviceName}] Setting CONNECTION_MODE to {_connectionMode}");
                if (!await SetSwitchValueAsync("CONNECTION_MODE", _connectionMode, true, TimeSpan.FromSeconds(10)))
                {
                    Logger.Error($"[{DeviceName}] Failed to set CONNECTION_MODE to {_connectionMode}");
                    return false;
                }
                Logger.Info($"[{DeviceName}] CONNECTION_MODE successfully set to {_connectionMode}");
            }

            if (IsUsingSerialMode)
            {
                Logger.Info($"[{DeviceName}] Configuring SERIAL mode connection");
                // Process SERIAL mode
                if (IsAutoMode)
                {
                    // Just set auto mode and return
                    Logger.Info($"[{DeviceName}] Enabling DEVICE_AUTO_SEARCH");
                    bool autoSearchResult = await SetSwitchValueAsync("DEVICE_AUTO_SEARCH", "INDI_ENABLED", true, TimeSpan.FromSeconds(10));
                    if (autoSearchResult)
                    {
                        Logger.Info($"[{DeviceName}] DEVICE_AUTO_SEARCH enabled successfully");
                    }
                    else
                    {
                        Logger.Error($"[{DeviceName}] Failed to enable DEVICE_AUTO_SEARCH");
                    }
                    return autoSearchResult;
                }

                // Disable auto mode (only if this device actually has the auto-search property —
                // secondary devices from a multi-device driver typically do not)
                if (_hasAutoSearchProperty)
                {
                    await SetSwitchValueAsync("DEVICE_AUTO_SEARCH", "INDI_DISABLED", true, TimeSpan.FromSeconds(10));
                }

                if (_hasDevicePortProperty)
                {
                    Logger.Info($"[{DeviceName}] Setting DEVICE_PORT to {_port}");
                    if (!await SetTextValueAsync("DEVICE_PORT", "PORT", _port, TimeSpan.FromSeconds(10)))
                    {
                        Logger.Error($"[{DeviceName}] Failed to set DEVICE_PORT to {_port}");
                        return false;
                    }
                    Logger.Info($"[{DeviceName}] DEVICE_PORT set to {_port}");
                }
                if (_hasDeviceBaudRateProperty)
                {
                    Logger.Info($"[{DeviceName}] Setting DEVICE_BAUD_RATE to {_baudRate}");
                    if (!await SetSwitchValueAsync("DEVICE_BAUD_RATE", $"{_baudRate}", true, TimeSpan.FromSeconds(10)))
                    {
                        Logger.Error($"[{DeviceName}] Failed to set DEVICE_BAUD_RATE to {_baudRate}");
                        return false;
                    }
                    Logger.Info($"[{DeviceName}] DEVICE_BAUD_RATE set to {_baudRate}");
                }
                Logger.Info($"[{DeviceName}] Serial mode configuration complete - DEVICE_PORT={_port}, BAUD_RATE={_baudRate}");
            }
            else if (IsUsingHttpMode)
            {
                Logger.Info($"[{DeviceName}] Configuring HTTP mode connection");
                // After switching CONNECTION_MODE, the driver may publish its address property
                // asynchronously. Wait briefly for either variant to appear.
                if (HasAddress)
                {
                    var addrPropStopwatch = System.Diagnostics.Stopwatch.StartNew();
                    while (!HasProperties(["DEVICE_BASE_URL"]) && !HasProperties(["DEVICE_ADDRESS"]) && addrPropStopwatch.Elapsed < TimeSpan.FromSeconds(10))
                    {
                        await CoreUtil.Wait(TimeSpan.FromMilliseconds(200));
                    }

                    if (HasProperties(["DEVICE_BASE_URL"]))
                    {
                        // Drivers like indi_starbook_ten use DEVICE_BASE_URL.BASE_URL with a full URL value.
                        var baseUrl = _address.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || _address.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                            ? _address
                            : $"http://{_address}";
                        Logger.Info($"[{DeviceName}] Setting DEVICE_BASE_URL to {baseUrl}");
                        if (!await SetTextValueAsync("DEVICE_BASE_URL", "BASE_URL", baseUrl, TimeSpan.FromSeconds(10)))
                        {
                            Logger.Error($"[{DeviceName}] Failed to set DEVICE_BASE_URL to {baseUrl}");
                            return false;
                        }
                        Logger.Info($"[{DeviceName}] HTTP mode configuration complete - BASE_URL={baseUrl}");
                    }
                    else if (HasProperties(["DEVICE_ADDRESS"]))
                    {
                        Logger.Info($"[{DeviceName}] Setting DEVICE_ADDRESS to {_address}");
                        if (!await SetTextValueAsync("DEVICE_ADDRESS", "ADDRESS", _address, TimeSpan.FromSeconds(10)))
                        {
                            Logger.Error($"[{DeviceName}] Failed to set DEVICE_ADDRESS to {_address}");
                            return false;
                        }
                        Logger.Info($"[{DeviceName}] HTTP mode configuration complete - ADDRESS={_address}");
                    }
                    else
                    {
                        Logger.Error($"[{DeviceName}] Neither DEVICE_BASE_URL nor DEVICE_ADDRESS appeared after switching to HTTP mode");
                        return false;
                    }
                }
            }
            else
            {
                Logger.Info($"[{DeviceName}] Configuring TCP mode connection");
                // Process TCP mode
                if (HasAddress)
                {
                    if (!_hasDeviceAddressProperty)
                    {
                        // The driver does not expose DEVICE_ADDRESS at all (e.g. a simulator in
                        // serial mode, or a device that reached this branch due to stale
                        // address/port in the profile). Nothing to write — skip, mirroring how
                        // the SERIAL branch gates writes on _hasDevicePortProperty.
                        Logger.Info($"[{DeviceName}] Device has no DEVICE_ADDRESS property - skipping TCP address configuration");
                    }
                    else
                    {
                        try
                        {
                            Logger.Info($"[{DeviceName}] Setting DEVICE_ADDRESS to {_address}:{_port}");
                            if (!await SetTextValueAsync("DEVICE_ADDRESS", "ADDRESS", _address, TimeSpan.FromSeconds(10)))
                            {
                                Logger.Error($"[{DeviceName}] Failed to set DEVICE_ADDRESS address to {_address}");
                                return false;
                            }
                            if (!await SetTextValueAsync("DEVICE_ADDRESS", "PORT", _port, TimeSpan.FromSeconds(10)))
                            {
                                Logger.Error($"[{DeviceName}] Failed to set DEVICE_ADDRESS port to {_port}");
                                return false;
                            }
                            Logger.Info($"[{DeviceName}] TCP mode configuration complete - ADDRESS={_address}:{_port}");
                        }
                        catch (Exception ex)
                        {
                            Logger.Error($"[{DeviceName}] Exception during TCP configuration: {ex.Message}");
                            return false;
                        }
                    }
                }
            }

            Logger.Info($"[{DeviceName}] OnPreConnect completed successfully");
            return true;
        }

        /// <summary>
        /// Check if all required properties have been received
        /// </summary>
        private bool HasProperties(string[] props)
        {
            if (props == null || props.Length == 0)
            {
                return true;
            }

            lock (_properties)
            {
                foreach (var propName in props)
                {
                    if (!_properties.ContainsKey(propName))
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        public virtual void OnSwitchPropertyUpdated(INDISwitchProperty p)
        {
            /*
            Logger.Info($"{p.Name}, {p.Label}, {p.State}, {p.Rule}");
            foreach (var s in p.Switches)
            {
                Logger.Info($"        {s.Name}, {s.Label}, {s.Value}");
            }
            */
            // Track CONNECTION failures to prevent spurious DISCONNECT attempts
            if (p.Name == "CONNECTION")
            {
                // Only treat an Alert as a failed attempt while a CONNECTION operation from
                // this instance is actually in flight — a re-broadcast of a pre-existing
                // Alert state (e.g. left over from an earlier failure) must not mark a
                // fresh attempt as failed. An actual mid-session disconnect is handled by
                // the CONNECT-switch check in DisconnectAsync, not by this flag.
                if (p.State == PropertyState.Alert && HasPendingOperationFor("CONNECTION"))
                {
                    _connectionAttemptFailed = true;
                    Logger.Warning($"Device '{DeviceName}' connection attempt failed (Alert state)");
                }

                // Sync _connected with the actual INDI CONNECTION switch state so that
                // an external disconnect (e.g. another device role using the same INDI driver
                // sent DISCONNECT) is reflected here without waiting for a poll.
                // Only act when there is no pending CONNECTION operation on this instance —
                // if there is one, the update is the ack for our own DISCONNECT command and
                // DisconnectAsync will set _connected = false itself once it completes.
                var connectSwitch = p.Switches.FirstOrDefault(s => s.Name == "CONNECT");
                if (connectSwitch != null && !connectSwitch.Value && _connected)
                {
                    if (!HasPendingOperationFor("CONNECTION"))
                    {
                        Logger.Warning($"Device '{DeviceName}' CONNECTION property shows DISCONNECT while we " +
                                       "thought it was connected — marking as externally disconnected");
                        _connected = false;
                    }
                }
            }

            // Check if there are any pending async operations for this property
            lock (_asyncOperationsLock)
            {
                // Find all pending operations for this property
                var operationsForProperty = _pendingAsyncOperations
                    .Where(kvp => kvp.Value.PropertyName == p.Name)
                    .ToList();

                foreach (var kvp in operationsForProperty)
                {
                    var operationId = kvp.Key;
                    var (tcs, _, preSendTimestamp, desiredReached) = kvp.Value;

                    // Resolve based on property state:
                    // - Busy: server has acknowledged the command and is processing it
                    // - Ok: operation completed successfully (some drivers skip Busy and go straight to Ok)
                    // - Alert: server rejected the command (but see stale-Alert guard below)
                    // - Idle: operation still pending, keep waiting
                    if (p.State == PropertyState.Busy)
                    {
                        if (!tcs.Task.IsCompleted)
                        {
                            Logger.Debug($"Async operation {operationId} acknowledged by server (state: Busy)");
                            tcs.TrySetResult(true);
                        }
                    }
                    else if (p.State == PropertyState.Ok)
                    {
                        // Guard against stale Ok: the server may re-broadcast the Ok from a
                        // previous operation while the new command is still being queued.
                        // INDI timestamps only have 1-second resolution, so a genuine fast
                        // response can carry the same timestamp as the pre-send state —
                        // in that case we fall back to checking whether the requested value has
                        // actually been applied (DesiredReached) before discarding it.
                        bool timestampChanged = string.IsNullOrEmpty(preSendTimestamp) || p.Timestamp != preSendTimestamp;
                        bool reached = desiredReached != null && desiredReached(p);

                        if (!timestampChanged && !reached)
                        {
                            Logger.Debug($"Async operation {operationId} ignoring stale Ok (unchanged timestamp: {p.Timestamp})");
                        }
                        else if (!tcs.Task.IsCompleted)
                        {
                            Logger.Debug($"Async operation {operationId} completed by server (state: Ok)");
                            tcs.TrySetResult(true);
                        }
                    }
                    else if (p.State == PropertyState.Alert)
                    {
                        // Guard against stale Alerts: if the timestamp hasn't changed the server
                        // is re-broadcasting a pre-existing Alert state, not rejecting our command.
                        if (!string.IsNullOrEmpty(preSendTimestamp) && p.Timestamp == preSendTimestamp)
                        {
                            Logger.Debug($"Async operation {operationId} ignoring stale Alert (unchanged timestamp: {p.Timestamp})");
                        }
                        else if (!tcs.Task.IsCompleted)
                        {
                            Logger.Warning($"Async operation {operationId} rejected by server (state: Alert)");
                            tcs.TrySetResult(false);
                        }
                    }
                    else if (p.State == PropertyState.Idle)
                    {
                        // libindi acks a successful DISCONNECT with state=Idle, not Ok
                        // (defaultdevice.cpp: "Disconnection is successful, set it IDLE").
                        // Idle alone is ambiguous (it is also the resting state), so only
                        // complete when the requested value is verifiably applied.
                        if (desiredReached != null && desiredReached(p) && !tcs.Task.IsCompleted)
                        {
                            Logger.Debug($"Async operation {operationId} completed by server (state: Idle, desired value reached)");
                            tcs.TrySetResult(true);
                        }
                    }
                }
            }
        }

        public virtual void OnNumberPropertyUpdated(INDINumberProperty p)
        {
            /*
            Logger.Info($"{p.Name}, {p.Label}, {p.State}");
            foreach (var n in p.Numbers) {
                Logger.Info($"        {n.Name}, {n.Label}, {n.Value}, {n.Min}, {n.Max}, {n.Step}, {n.Format}");
            }
            */

            // Check if there are any pending async operations for this property
            lock (_asyncOperationsLock)
            {
                // Find all pending operations for this property
                var operationsForProperty = _pendingAsyncOperations
                    .Where(kvp => kvp.Value.PropertyName == p.Name)
                    .ToList();

                foreach (var kvp in operationsForProperty)
                {
                    var operationId = kvp.Key;
                    var (tcs, _, preSendTimestamp, desiredReached) = kvp.Value;

                    // Resolve based on property state:
                    // - Busy: server has acknowledged the command and is processing it
                    // - Ok: operation completed successfully (some drivers skip Busy and go straight to Ok)
                    // - Alert: server rejected the command (but see stale-Alert guard below)
                    // - Idle: operation still pending, keep waiting
                    if (p.State == PropertyState.Busy)
                    {
                        if (!tcs.Task.IsCompleted)
                        {
                            Logger.Debug($"Async operation {operationId} acknowledged by server (state: Busy)");
                            tcs.TrySetResult(true);
                        }
                    }
                    else if (p.State == PropertyState.Ok)
                    {
                        // Guard against stale Ok (see OnSwitchPropertyUpdated for details).
                        bool timestampChanged = string.IsNullOrEmpty(preSendTimestamp) || p.Timestamp != preSendTimestamp;
                        bool reached = desiredReached != null && desiredReached(p);

                        if (!timestampChanged && !reached)
                        {
                            Logger.Debug($"Async operation {operationId} ignoring stale Ok (unchanged timestamp: {p.Timestamp})");
                        }
                        else if (!tcs.Task.IsCompleted)
                        {
                            Logger.Debug($"Async operation {operationId} completed by server (state: Ok)");
                            tcs.TrySetResult(true);
                        }
                    }
                    else if (p.State == PropertyState.Alert)
                    {
                        // Guard against stale Alerts: if the timestamp hasn't changed the server
                        // is re-broadcasting a pre-existing Alert state, not rejecting our command.
                        if (!string.IsNullOrEmpty(preSendTimestamp) && p.Timestamp == preSendTimestamp)
                        {
                            Logger.Debug($"Async operation {operationId} ignoring stale Alert (unchanged timestamp: {p.Timestamp})");
                        }
                        else if (!tcs.Task.IsCompleted)
                        {
                            Logger.Warning($"Async operation {operationId} rejected by server (state: Alert)");
                            tcs.TrySetResult(false);
                        }
                    }
                    else if (p.State == PropertyState.Idle)
                    {
                        // Some drivers ack with state=Idle (libindi uses it for successful
                        // disconnects). Idle alone is ambiguous, so only complete when the
                        // requested value is verifiably applied.
                        if (desiredReached != null && desiredReached(p) && !tcs.Task.IsCompleted)
                        {
                            Logger.Debug($"Async operation {operationId} completed by server (state: Idle, desired value reached)");
                            tcs.TrySetResult(true);
                        }
                    }
                }
            }
        }

        public virtual void OnTextPropertyUpdated(INDITextProperty p)
        {
            /*
            Logger.Info($"{p.Name}, {p.Label}, {p.State}");
            foreach (var t in p.Texts) {
                Logger.Info($"        {t.Name}, {t.Label}, {t.Value}");
            }
            */

            // Check if there are any pending async operations for this property
            lock (_asyncOperationsLock)
            {
                // Find all pending operations for this property
                var operationsForProperty = _pendingAsyncOperations
                    .Where(kvp => kvp.Value.PropertyName == p.Name)
                    .ToList();

                foreach (var kvp in operationsForProperty)
                {
                    var operationId = kvp.Key;
                    var (tcs, _, preSendTimestamp, desiredReached) = kvp.Value;

                    // Resolve based on property state:
                    // - Busy: server has acknowledged the command and is processing it
                    // - Ok: operation completed successfully (some drivers skip Busy and go straight to Ok)
                    // - Alert: server rejected the command (but see stale-Alert guard below)
                    // - Idle: operation still pending, keep waiting
                    if (p.State == PropertyState.Busy)
                    {
                        if (!tcs.Task.IsCompleted)
                        {
                            Logger.Debug($"Async operation {operationId} acknowledged by server (state: Busy)");
                            tcs.TrySetResult(true);
                        }
                    }
                    else if (p.State == PropertyState.Ok)
                    {
                        // Guard against stale Ok (see OnSwitchPropertyUpdated for details).
                        bool timestampChanged = string.IsNullOrEmpty(preSendTimestamp) || p.Timestamp != preSendTimestamp;
                        bool reached = desiredReached != null && desiredReached(p);

                        if (!timestampChanged && !reached)
                        {
                            Logger.Debug($"Async operation {operationId} ignoring stale Ok (unchanged timestamp: {p.Timestamp})");
                        }
                        else if (!tcs.Task.IsCompleted)
                        {
                            Logger.Debug($"Async operation {operationId} completed by server (state: Ok)");
                            tcs.TrySetResult(true);
                        }
                    }
                    else if (p.State == PropertyState.Alert)
                    {
                        // Guard against stale Alerts: if the timestamp hasn't changed the server
                        // is re-broadcasting a pre-existing Alert state, not rejecting our command.
                        if (!string.IsNullOrEmpty(preSendTimestamp) && p.Timestamp == preSendTimestamp)
                        {
                            Logger.Debug($"Async operation {operationId} ignoring stale Alert (unchanged timestamp: {p.Timestamp})");
                        }
                        else if (!tcs.Task.IsCompleted)
                        {
                            Logger.Warning($"Async operation {operationId} rejected by server (state: Alert)");
                            tcs.TrySetResult(false);
                        }
                    }
                }
            }
        }

        public virtual void OnBlobPropertyUpdated(INDIBlobProperty p)
        {
        }

        public virtual void OnLightPropertyUpdated(INDILightProperty p)
        {
        }

        public INDILightProperty GetLightProperty(string propertyName)
        {
            return GetProperty(propertyName) as INDILightProperty;
        }

        public PropertyState? GetLightPropertyState(string propertyName)
        {
            return GetLightProperty(propertyName)?.State;
        }

        private string _connectionMode;
        private bool _autoSearch;
        private string _address;
        private string _port;
        private int _baudRate;
        private TimeSpan _preConnectDelay = TimeSpan.Zero;

        public void ConfigureConnectionProperties(string connectionMode, bool autoSearch, string address, string port, int baudRate)
        {
            _connectionMode = connectionMode;
            _autoSearch = autoSearch;
            _address = address;
            _port = port;
            _baudRate = baudRate;
        }

        public void ConfigurePreConnectDelay(TimeSpan delay)
        {
            _preConnectDelay = delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
        }

        #region Unsupported
        public virtual IList<string> SupportedActions => new List<string>();

        public virtual string Action(string actionName, string actionParameters)
        {
            throw new NotImplementedException();
        }
        #endregion

        #region Raw LX200 TCP commands
        // Persistent TCP connection to the mount for raw LX200 commands.
        // A single connection is reused across all commands; it is re-established if broken.
        private TcpClient _lx200Client;
        private NetworkStream _lx200Stream;
        private readonly object _lx200Lock = new object();

        private int GetTcpPort()
        {
            if (string.IsNullOrEmpty(_address) || string.IsNullOrEmpty(_port))
            {
                throw new InvalidOperationException("Cannot send raw command: device is not configured for TCP mode (no address/port)");
            }
            if (!int.TryParse(_port, out int port))
            {
                throw new InvalidOperationException($"Cannot send raw command: invalid port '{_port}'");
            }
            return port;
        }

        private NetworkStream EnsureLx200Stream()
        {
            if (_lx200Client != null && _lx200Client.Connected)
            {
                return _lx200Stream;
            }
            _lx200Client?.Dispose();
            _lx200Client = null;
            _lx200Stream = null;
            var client = new TcpClient();
            try
            {
                client.SendTimeout = 3000;
                client.ReceiveTimeout = 3000;
                client.Connect(_address, GetTcpPort());
            }
            catch
            {
                client.Dispose();
                throw;
            }
            _lx200Client = client;
            _lx200Stream = client.GetStream();
            return _lx200Stream;
        }

        private void DisposeLx200Connection()
        {
            _lx200Stream?.Dispose();
            _lx200Client?.Dispose();
            _lx200Stream = null;
            _lx200Client = null;
        }

        private void SendRawTcpBlind(string command)
        {
            lock (_lx200Lock)
            {
                var stream = EnsureLx200Stream();
                var bytes = Encoding.ASCII.GetBytes(command);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
            }
        }

        private bool SendRawTcpCommandBool(string command)
        {
            lock (_lx200Lock)
            {
                var stream = EnsureLx200Stream();
                var bytes = Encoding.ASCII.GetBytes(command);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
                // Boolean LX200 responses are a single '0' or '1' with no '#' terminator
                int b = stream.ReadByte();
                return b == '1';
            }
        }

        private string SendRawTcpCommand(string command)
        {
            lock (_lx200Lock)
            {
                var stream = EnsureLx200Stream();
                var bytes = Encoding.ASCII.GetBytes(command);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
                // Read until '#' terminator
                var response = new StringBuilder();
                while (true)
                {
                    int b = stream.ReadByte();
                    if (b == -1) break;
                    var ch = (char)b;
                    response.Append(ch);
                    if (ch == '#') break;
                }
                return response.ToString();
            }
        }

        public virtual void CommandBlind(string command, bool raw = false)
        {
            if (string.IsNullOrEmpty(_address) || _connectionMode != "CONNECTION_TCP")
            {
                throw new NotImplementedException();
            }
            try
            {
                SendRawTcpBlind(command);
            }
            catch (Exception)
            {
                // Take the lock: another thread may be mid-command on the same stream, and
                // disposing under it would turn its failure into an ObjectDisposedException
                // at a random point instead of a clean send/receive error.
                lock (_lx200Lock)
                {
                    DisposeLx200Connection();
                }
                throw;
            }
        }

        public virtual bool CommandBool(string command, bool raw = false)
        {
            if (string.IsNullOrEmpty(_address) || _connectionMode != "CONNECTION_TCP")
            {
                throw new NotImplementedException();
            }
            try
            {
                return SendRawTcpCommandBool(command);
            }
            catch (Exception)
            {
                // Take the lock: another thread may be mid-command on the same stream, and
                // disposing under it would turn its failure into an ObjectDisposedException
                // at a random point instead of a clean send/receive error.
                lock (_lx200Lock)
                {
                    DisposeLx200Connection();
                }
                throw;
            }
        }

        public virtual string CommandString(string command, bool raw = false)
        {
            if (string.IsNullOrEmpty(_address) || _connectionMode != "CONNECTION_TCP")
            {
                throw new NotImplementedException();
            }
            try
            {
                return SendRawTcpCommand(command);
            }
            catch (Exception)
            {
                // Take the lock: another thread may be mid-command on the same stream, and
                // disposing under it would turn its failure into an ObjectDisposedException
                // at a random point instead of a clean send/receive error.
                lock (_lx200Lock)
                {
                    DisposeLx200Connection();
                }
                throw;
            }
        }
        #endregion
    }
}