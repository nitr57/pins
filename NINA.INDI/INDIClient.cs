#region "copyright"

/*
    Copyright © 2025-2026 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Core.Utility;
using NINA.INDI.Devices;
using NINA.INDI.Enums;
using NINA.INDI.Protocol;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace NINA.INDI {
    public class INDIDeviceInfo {
        public string Id { get; set; }
        public string Name { get; set; }
        public DeviceInterface Interface { get; set; }
        public string Version;
        public string Driver;
    }

    public class INDIClient : IDisposable {
        private static INDIClient _instance;
        private static readonly object _lock = new();

        public static INDIClient Instance {
            get {
                if (_instance == null) {
                    lock (_lock) {
                        if (_instance == null) {
                            _instance = new INDIClient(7624);
                        }
                    }
                }
                return _instance;
            }
        }

        private readonly int _port;
        private TcpClient _tcpClient;
        private NetworkStream _stream;
        private Task _receiveTask;
        private CancellationTokenSource _cts;
        private TaskCompletionSource<bool> _driverTcs;
        // The driver exec name LoadDriver is currently waiting on. Guards _driverTcs completion
        // in CheckForNewDevice so a DRIVER_INFO re-broadcast from an unrelated, already-loaded
        // driver can't complete a DIFFERENT in-flight LoadDriver prematurely (guarded by
        // _driverLock, same as _driverTcs).
        private string _pendingLoadDriverExec;
        private readonly object _operationLock = new();
        private readonly object _driverLock = new();
        private readonly SemaphoreSlim _getDriversSemaphore = new(1, 1);

        // Signals when the server is ready for driver operations
        private readonly TaskCompletionSource<bool> _serverReadyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly Dictionary<string, INDIDeviceInfo> _discoveredDevices = [];
        // Multiple devices can share the same INDI device name when the driver exposes
        // both a telescope and a focuser interface (e.g. indi_lx200generic).
        // All registered instances receive property updates so that an external disconnect
        // from one role is correctly reflected in the other.
        private readonly Dictionary<string, List<INDIDevice>> _registeredDevices = [];

        // Driver executable names currently running under indiserver. A single driver (e.g.
        // indi_lx200generic) can expose devices for multiple interfaces (telescope + focuser);
        // eviction is scoped by NINA device-type category (_lastDriverPerCategory) rather than
        // by interface, so a rescan of one category never stops a driver still serving another.
        private readonly HashSet<string> _loadedDrivers = [];

        // Maps NINA device-type category name (e.g. "WeatherData", "SafetyMonitor") to the
        // driver that was last loaded for that category.  Eviction during a rescan only touches
        // the driver previously loaded for the SAME category, so two categories that share an
        // INDI interface flag (e.g. WEATHER_INTERFACE) never evict each other's drivers.
        private readonly Dictionary<string, string> _lastDriverPerCategory = [];

        // Global property store: deviceName -> (propertyName -> property). Captures every property
        // for every device currently visible to indiserver, regardless of whether a NINA equipment
        // adapter (INDIDevice) registered for it. This is what powers the generic INDI control panel,
        // which must be able to inspect/modify devices NINA itself never connected (e.g. a guide
        // camera driven by PHD2). Property objects are the SAME references handed to registered
        // INDIDevice instances, so in-place set* updates are reflected here automatically.
        // Guarded by _lock (same lock ProcessElement already holds while mutating routing state).
        private readonly Dictionary<string, Dictionary<string, INDIProperty>> _allProperties = [];

        // Rolling buffer of human-readable INDI <message> elements (driver/server log lines).
        // Kept independent of the property store so the control panel can show a live log tail.
        private const int MaxMessages = 500;
        private readonly List<INDIMessage> _messages = [];
        private readonly object _messagesLock = new();

        // Private: the class manages a machine-wide indiserver (pkill on start, a fixed FIFO
        // path), so a second instance would fight the first. Use INDIClient.Instance.
        private INDIClient(int port) {
            if (port < 1 || port > 65535) {
                throw new ArgumentOutOfRangeException(nameof(port), "Port must be between 1 and 65535.");
            }

            _port = port;
            Task.Run(async () => await StartServerInFifoMode());
        }

        public bool IsConnected => _tcpClient?.Connected ?? false;

        public async Task<bool> Connect(CancellationToken ct = default) {
            if (IsConnected) {
                Logger.Warning("INDI client already connected to server");
                return true;
            }

            try {
                // Check cancellation before starting
                ct.ThrowIfCancellationRequested();

                // Attach client
                _tcpClient = new TcpClient();
                await _tcpClient.ConnectAsync("localhost", _port, ct);
                _stream = _tcpClient.GetStream();

                // Any previous session's CTS was cancelled by Disconnect (or its loop already
                // exited on a read fault); dispose it before replacing.
                _cts?.Dispose();
                _cts = new CancellationTokenSource();
                // Hand the loop ITS stream as a parameter — it must never re-read the _stream
                // field, or a stale loop from a previous session could read the new session's
                // stream (two concurrent readers) after a quick Disconnect→Connect cycle.
                var stream = _stream;
                var token = _cts.Token;
                _receiveTask = Task.Run(() => ReceiveLoop(stream, token));

                // Request all properties from all devices
                GetProperties();

                if (IsConnected) {
                    Logger.Info("Connected to INDI server");
                } else {
                    Logger.Error("Failed to connect to INDI server");
                }
                return IsConnected;
            } catch (OperationCanceledException) {
                Logger.Warning("INDI bus client attachment was cancelled");
                _tcpClient?.Dispose();
                _tcpClient = null;
                _stream = null;
                return false;
            } catch (Exception ex) {
                Logger.Error($"Exception attaching INDI bus client: {ex.Message}");
                // Dispose the failed client so startup retries don't leak one socket per attempt.
                _tcpClient?.Dispose();
                _tcpClient = null;
                _stream = null;
                return false;
            }
        }

        public void Disconnect() {
            if (!IsConnected) {
                Logger.Warning("INDI not connected to server");
                return;
            }

            try {
                // Unload drivers - devices will see IsConnected=false and skip waiting
                // Only try to gracefully unload drivers if server process is still alive
                if (_process != null && !_process.HasExited) {
                    List<string> driversToUnload;
                    lock (_driverLock) {
                        driversToUnload = _loadedDrivers.ToList();
                    }
                    foreach (var driverName in driversToUnload) {
                        UnloadDriver(driverName);
                    }
                } else {
                    Logger.Debug("INDI server process not running, skipping graceful driver unload");
                    lock (_driverLock) {
                        _loadedDrivers.Clear();
                        // The server is gone, so no delProperty will ever evict these. Leaving
                        // them would make IsDeviceKnown keep returning true, which defeats the
                        // driver-was-unloaded bail-out in INDIDevice.DisconnectAsync and costs
                        // a full DISCONNECT timeout per device on shutdown after a server crash.
                        _discoveredDevices.Clear();
                    }
                }

                // Cancel receive loop and close connection FIRST
                // This makes IsConnected return false immediately
                _cts?.Cancel();
                _stream?.Close();
                _tcpClient?.Close();
                _stream = null;
                _tcpClient = null;

                // Wait (bounded) for the receive loop to actually exit: it may still be
                // draining a large buffered element, and ProcessXmlMessage writes the shared
                // big-tag scanner state — a Connect issued right after this Disconnect must
                // not race those writes (the new loop resets that state on entry).
                var receiveTask = _receiveTask;
                _receiveTask = null;
                if (receiveTask != null && !receiveTask.IsCompleted) {
                    try {
                        receiveTask.Wait(TimeSpan.FromSeconds(2));
                    } catch (AggregateException) {
                        // Faulted or cancelled — either way it has exited, which is all we need.
                    }
                }

                // Drop the cached property snapshot — once the receive loop is cancelled no more
                // delProperty messages will arrive to evict stale devices from the control panel.
                lock (_lock) {
                    _allProperties.Clear();
                }
                Logger.Info("INDI client disconnected");
            } catch (Exception ex) {
                Logger.Error($"Exception disconnecting from INDI server: {ex.Message}");
            }
        }

        private async Task<bool> LoadDriver(string driverName, DeviceInterface deviceInterface, TimeSpan? loadTimeout = null, CancellationToken ct = default) {
            // Eviction of the previous driver for this category is already handled by
            // GetDevices (by driver name, scoped to the NINA device-type category).
            // Do NOT call UnloadDriver(deviceInterface) here — that would blindly evict
            // drivers for OTHER categories that share the same interface flag (e.g.
            // WeatherData and SafetyMonitor both use WEATHER_INTERFACE).

            // We explicitly allow empty string to NOT load any driver
            if (string.IsNullOrEmpty(driverName) || driverName.Equals("None")) {
                return true;
            }

            bool alreadyLoaded;
            lock (_driverLock) {
                alreadyLoaded = _loadedDrivers.Contains(driverName);
            }
            if (alreadyLoaded) {
                // Do nothing
                return true;
            }

            try {
                lock (_driverLock) {
                    _driverTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _pendingLoadDriverExec = driverName;
                }

                ct.ThrowIfCancellationRequested();

                if (!WriteFifoCommand($"start {driverName}", TimeSpan.FromSeconds(5))) {
                    return false;
                }

                // The start command is irrevocably sent at this point — track the driver as
                // loaded NOW, not only once it describes itself. A slow driver (camera SDK
                // enumeration can exceed the wait below) is running even when the DRIVER_INFO
                // wait times out; leaving it untracked made every rescan write another "start"
                // to the FIFO (duplicate driver instance risk) and skipped it during graceful
                // unload. The wait below is purely "did it describe itself in time".
                lock (_driverLock) {
                    _loadedDrivers.Add(driverName);
                }

                // Wait for the driver to be loaded with timeout and cancellation support
                var timeout = loadTimeout ?? TimeSpan.FromSeconds(15);
                var timeoutTask = Task.Delay(timeout, ct);
                var completedTask = await Task.WhenAny(_driverTcs.Task, timeoutTask);

                if (completedTask == timeoutTask) {
                    // Check if it was a timeout or cancellation
                    if (ct.IsCancellationRequested) {
                        Logger.Warning($"INDI loading driver {driverName} was cancelled");
                        return false;
                    }
                    Logger.Error($"INDI loading driver {driverName} timed out waiting for DRIVER_INFO (driver stays tracked as loaded — the start command was already sent)");
                    return false;
                }

                bool success = await _driverTcs.Task;
                if (success) {
                    Logger.Info($"Loaded driver '{driverName}' ({deviceInterface})");
                }

                return success;
            } catch (OperationCanceledException) {
                Logger.Warning($"INDI loading driver {driverName} was cancelled");
                return false;
            } catch (Exception ex) {
                Logger.Error($"Exception loading INDI driver: {ex.Message}");
                return false;
            }
        }

        private void UnloadDriver(string driverName) {
            // We explicitly allow empty string to NOT load/unload any driver
            if (string.IsNullOrEmpty(driverName) || driverName.Equals("None")) {
                return;
            }

            bool isLoaded;
            lock (_driverLock) {
                isLoaded = _loadedDrivers.Contains(driverName);
            }

            if (!isLoaded) {
                Logger.Warning($"Driver '{driverName}' is not loaded");
                return;
            }

            Logger.Debug($"Removing driver '{driverName}'");

            // Write to the FIFO outside _driverLock — see WriteFifoCommand for why the
            // blocking open() must be bounded and must never run while a lock is held.
            if (!WriteFifoCommand($"stop {driverName}", TimeSpan.FromSeconds(5))) {
                return;
            }

            lock (_driverLock) {
                _loadedDrivers.Remove(driverName);

                // Remove devices associated with this driver
                var devicesToRemove = _discoveredDevices.Where(d => d.Value.Driver == driverName).Select(d => d.Key).ToList();
                foreach (var deviceKey in devicesToRemove) {
                    _discoveredDevices.Remove(deviceKey);
                    Logger.Debug($"Removed device '{deviceKey}' (driver: {driverName})");
                }
            }
            Logger.Info($"Unloaded driver '{driverName}'");
        }

        /// <summary>
        /// Writes a single "start &lt;driver&gt;" / "stop &lt;driver&gt;" command line to the
        /// indiserver FIFO. On Linux, opening a FIFO write-only blocks in the underlying open()
        /// syscall until a reader is present; if indiserver has hung or died, that open never
        /// returns. Runs the open+write on a background task and bounds the wait with a timeout
        /// so a dead server can never freeze the caller (or, more importantly, any lock it holds).
        /// </summary>
        private bool WriteFifoCommand(string command, TimeSpan timeout) {
            try {
                // When the open() blocks past the timeout the task is abandoned but stays
                // stuck in the syscall. If a reader appears much later (indiserver restart),
                // the open would then succeed and write a command the caller gave up on long
                // ago — e.g. starting a driver the user has since deselected. The abandoned
                // flag makes the late writer discard the command instead.
                var abandoned = new bool[1];
                var writeTask = Task.Run(() => {
                    using var fs = new FileStream(_fifoPath, FileMode.Open, FileAccess.Write);
                    if (Volatile.Read(ref abandoned[0])) {
                        Logger.Warning($"Discarding stale INDI FIFO command '{command}' — the FIFO only became writable after the command had already timed out");
                        return;
                    }
                    using var writer = new StreamWriter(fs);
                    writer.WriteLine(command);
                    writer.Flush();
                });

                if (!writeTask.Wait(timeout)) {
                    Volatile.Write(ref abandoned[0], true);
                    Logger.Error($"Timed out writing '{command}' to INDI FIFO ({_fifoPath}) after {timeout.TotalSeconds:F0}s — indiserver may be hung");
                    return false;
                }
                return true;
            } catch (Exception ex) {
                Logger.Error($"Failed writing '{command}' to INDI FIFO: {ex.Message}");
                return false;
            }
        }

        internal void RegisterDevice(INDIDevice device) {
            lock (_lock) {
                if (!_registeredDevices.TryGetValue(device.Id, out var list)) {
                    list = [];
                    _registeredDevices[device.Id] = list;
                }
                if (!list.Contains(device)) {
                    list.Add(device);
                }
                Logger.Debug($"Registered device: '{device.Id}' (Name: '{device.DeviceName}', total for this id: {list.Count})");
            }
        }

        internal void UnregisterDevice(INDIDevice device) {
            lock (_lock) {
                if (_registeredDevices.TryGetValue(device.Id, out var list)) {
                    list.Remove(device);
                    if (list.Count == 0) {
                        _registeredDevices.Remove(device.Id);
                    }
                    Logger.Debug($"Unregistered device: '{device.Id}'");
                }
            }
        }

        /// <summary>
        /// Returns the live registered device of type <typeparamref name="T"/> (e.g.
        /// <c>INDITelescope</c>), or null when none is registered. When
        /// <paramref name="deviceName"/> is supplied, only a device whose name matches
        /// (case-insensitive) is returned. Gives callers outside NINA.INDI a handle to the
        /// concrete device adapter so they can use its typed API rather than the raw
        /// property store.
        /// </summary>
        public T GetRegisteredDevice<T>(string deviceName = null) where T : INDIDevice {
            lock (_lock) {
                return _registeredDevices.Values
                    .SelectMany(list => list)
                    .OfType<T>()
                    .FirstOrDefault(d => string.IsNullOrWhiteSpace(deviceName)
                        || string.Equals(d.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));
            }
        }

        /// <summary>
        /// Returns true when the given device ID is still present in the discovered-devices
        /// table (i.e. the driver is still loaded and the device is visible to indiserver).
        /// Used by DisconnectAsync to bail out early when the driver was killed before
        /// the graceful DISCONNECT handshake could complete.
        /// </summary>
        public bool IsDeviceKnown(string id) {
            lock (_driverLock) {
                return _discoveredDevices.ContainsKey(id);
            }
        }

        public void GetProperties(string device = null, string name = null) {
            if (!IsConnected) {
                Logger.Warning("Cannot enumerate properties: not attached to INDI server");
                return;
            }

            var element = new XElement("getProperties");
            element.Add(new XAttribute("version", "1.7"));
            if (device != null) {
                element.Add(new XAttribute("device", device));
            }
            if (name != null) {
                element.Add(new XAttribute("name", name));
            }
            SendMessage(element);
        }

        private void SendMessage(XElement element) {
            if (!IsConnected) {
                return;
            }

            lock (_operationLock) {
                try {
                    var xml = element.ToString(SaveOptions.DisableFormatting);
                    var bytes = Encoding.UTF8.GetBytes(xml);
                    _stream.Write(bytes, 0, bytes.Length);
                    _stream.Flush();
                } catch (Exception ex) {
                    Logger.Error($"Send error: {ex.Message}");
                }
            }
        }

        public void SendProperty(INDIProperty prop) {
            SendMessage(prop.ToXml());
        }

        public void EnableBLOB(string deviceName) {
            var element = new XElement("enableBLOB",
                new XAttribute("device", deviceName),
                "Also");
            SendMessage(element);
            Logger.Debug($"Sent enableBLOB for device '{deviceName}'");
        }

        // State for the O(n) large-element fast path in ProcessXmlMessage.
        private string _pendingBigTagName = null;
        private int _pendingBigTagSearchFrom = 0;

        // The stream is a parameter on purpose: this loop must only ever read the stream it
        // was started with. Re-reading the _stream field would let a loop from a previous
        // session pick up (and corrupt) the NEW session's stream after a fast
        // Disconnect→Connect cycle.
        private async Task ReceiveLoop(NetworkStream stream, CancellationToken ct) {
            if (stream == null) return;

            // The scanner state below is per-connection, but lives in instance fields (the
            // xmlBuffer is local). If the previous connection died mid-large-element, stale
            // big-tag state would make this session wait for an end tag (and a buffer offset)
            // belonging to the old stream — buffering everything and delivering nothing.
            _pendingBigTagName = null;
            _pendingBigTagSearchFrom = 0;

            var buffer = new byte[1024 * 1024]; // 1 MB — fewer iterations for multi-MB BLOBs
            var xmlBuffer = new StringBuilder();
            // A stateful decoder carries an incomplete multi-byte sequence across reads.
            // Encoding.UTF8.GetString per-chunk would turn a character split across two TCP
            // reads (driver labels/messages with °, µ, non-English text) into replacement
            // characters, which can corrupt XML structure if it lands inside a tag.
            var decoder = Encoding.UTF8.GetDecoder();
            // Reused decode buffer — allocating a fresh char[] per read churned up to 2 MB
            // of garbage per iteration during BLOB transfers (noticeable on a Pi).
            var charBuffer = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];

            while (!ct.IsCancellationRequested) {
                try {
                    var bytesRead = await stream.ReadAsync(buffer, ct);
                    if (bytesRead == 0) {
                        Logger.Error("Server disconnected");
                        break;
                    }

                    var charCount = decoder.GetChars(buffer, 0, bytesRead, charBuffer, 0);
                    AppendValidXmlChars(xmlBuffer, charBuffer, charCount);

                    // Process complete XML elements
                    ProcessXmlMessage(xmlBuffer);
                } catch (Exception ex) when (ex is not OperationCanceledException) {
                    if (ct.IsCancellationRequested) {
                        // Normal Disconnect: the stream was closed under us, so the read
                        // faulted (ObjectDisposedException etc.) — not a receive error.
                        break;
                    }
                    Logger.Error($"Receive error: {ex.Message}");
                    break;
                }
            }
        }

        /// <summary>
        /// Enumerates devices for the given interface, loading <paramref name="driver"/> if needed.
        /// Note: only ONE driver is tracked per <paramref name="deviceTypeCategory"/> — requesting a
        /// different driver for the same category evicts (unloads) the previously loaded one. Two
        /// devices of the same category served by different drivers cannot be connected concurrently.
        /// </summary>
        public async Task<IReadOnlyList<INDIDeviceInfo>> GetDevices(DeviceInterface deviceInterface, string driver, string deviceTypeCategory = null, CancellationToken ct = default) {
            // Wait (bounded) for the server to be ready before proceeding. A missing or
            // broken INDI server must not hang device enumeration indefinitely.
            if (!await WaitForServerReadyAsync(TimeSpan.FromSeconds(15), ct)) {
                Logger.Debug($"INDI server not ready - skipping {deviceTypeCategory ?? deviceInterface.ToString()} enumeration");
                return [];
            }

            // Serialize GetDevices calls to prevent concurrent driver load/unload operations
            await _getDriversSemaphore.WaitAsync(ct);
            try {
                // Device list
                var devices = new Dictionary<string, INDIDeviceInfo>();

                // Empty string or null means no indi driver to be loaded
                if (!string.IsNullOrEmpty(driver)) {
                    // If driver is not yet loaded, load it — but only evict the driver that
                    // was previously loaded for THIS specific NINA device-type category.
                    // This prevents a SafetyMonitor rescan from evicting a WeatherData driver
                    // even though both share WEATHER_INTERFACE.
                    bool driverAlreadyLoaded;
                    lock (_driverLock) {
                        driverAlreadyLoaded = _loadedDrivers.Contains(driver);
                    }
                    if (!driverAlreadyLoaded) {
                        if (deviceTypeCategory != null
                            && _lastDriverPerCategory.TryGetValue(deviceTypeCategory, out string oldDriver)
                            && oldDriver != driver) {
                            Logger.Info($"Evicting previous {deviceTypeCategory} driver '{oldDriver}' before loading '{driver}'");
                            UnloadDriver(oldDriver);
                        }
                        if (deviceTypeCategory != null) {
                            _lastDriverPerCategory[deviceTypeCategory] = driver;
                        }

                        ct.ThrowIfCancellationRequested();

                        // Use LoadDriver's default (15s) DRIVER_INFO wait. A 3s override used
                        // to live here, but camera drivers doing SDK enumeration routinely
                        // exceed that, which made the first scan come up empty.
                        await LoadDriver(driver, deviceInterface, ct: ct);

                        // Give multi-device drivers (e.g. indi_lx200generic which exposes both
                        // a telescope AND a focuser) a moment to deliver all their DRIVER_INFO
                        // messages.  The _driverTcs fires on the first DRIVER_INFO, so without
                        // this delay the loop below might not yet see the second device.
                        await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
                    }

                    // Fetch all discovered devices. Hold _driverLock while enumerating —
                    // the reader thread adds (DRIVER_INFO) and removes (device-level
                    // delProperty) entries under the same lock.
                    lock (_driverLock) {
                        foreach (var dev in _discoveredDevices) {
                            if ((dev.Value.Interface & deviceInterface) != 0) {
                                if (!devices.ContainsKey(dev.Key)) {
                                    Logger.Info($"Found device {dev.Key}");
                                    devices.Add(dev.Key, dev.Value);
                                }
                            }
                        }
                    }
                }

                return devices.Values.ToList();
            } finally {
                _getDriversSemaphore.Release();
            }
        }

        /// <summary>
        /// Wait for the INDI server to be ready. Returns true if the server
        /// reports ready (SetResult(true)), false if it reports not-ready or
        /// if waiting timed out or was cancelled.
        /// </summary>
        public async Task<bool> WaitForServerReadyAsync(TimeSpan? timeout = null, CancellationToken ct = default) {
            try {
                var serverReadyTask = _serverReadyTcs.Task;
                if (timeout.HasValue) {
                    var completed = await Task.WhenAny(serverReadyTask, Task.Delay(timeout.Value, ct));
                    if (completed != serverReadyTask) {
                        Logger.Debug("WaitForServerReadyAsync timed out");
                        return false;
                    }
                } else {
                    await serverReadyTask;
                }

                return serverReadyTask.IsCompleted && serverReadyTask.Result;
            } catch (OperationCanceledException) {
                Logger.Warning("WaitForServerReadyAsync cancelled");
                return false;
            } catch (Exception ex) {
                Logger.Error($"WaitForServerReadyAsync failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Return a human-readable version string for the indiserver process.
        /// May be empty if unknown. This is populated from the server's stdout
        /// banner or from the running process metadata where available.
        /// </summary>
        public string GetServerVersionString() {
            if (!string.IsNullOrWhiteSpace(_serverVersionString)) return _serverVersionString;
            // Fallback: return a tiny default if we have no info
            return IsConnected ? "indiserver (connected)" : "indiserver (unknown)";
        }

        /// <summary>
        /// Returns an optional platform version derived from the indiserver binary
        /// metadata (if readable). If not available returns Version(0,0,0,0).
        /// </summary>
        public Version GetServerPlatformVersion() {
            return _serverPlatformVersion ?? new Version(0, 0, 0, 0);
        }

        public void Dispose() {
            Logger.Info("INDIClient.Dispose() starting cleanup");
            Disconnect();
            CleanupServer();
            Logger.Info("INDIClient.Dispose() complete");
        }

        private void CheckForNewDevice(INDIProperty p) {
            if (p is INDITextProperty t) {
                // Device enumeration
                if (t.Name == "DRIVER_INFO") {
                    lock (_driverLock) {
                        string exec;

                        // Track this device
                        if (!_discoveredDevices.ContainsKey(t.DeviceName)) {
                            var id = p.DeviceName;

                            var name = string.Empty;
                            exec = string.Empty;
                            var version = string.Empty;
                            var deviceInterface = 0;

                            foreach (var text in t.Texts) {
                                switch (text.Name) {
                                    case "DRIVER_NAME":
                                        name = text.Value;
                                        break;
                                    case "DRIVER_EXEC":
                                        exec = text.Value;
                                        break;
                                    case "DRIVER_VERSION":
                                        version = text.Value;
                                        break;
                                    case "DRIVER_INTERFACE":
                                        if (int.TryParse(text.Value, out var interfaceNumber)) {
                                            deviceInterface = interfaceNumber;
                                        }
                                        break;
                                }
                            }

                            Logger.Info($"Found {name} INDI device ({exec}) v{version} with ID '{id}'");

                            _discoveredDevices.Add(t.DeviceName, new INDIDeviceInfo {
                                Id = id,
                                Name = name,
                                Interface = (DeviceInterface)deviceInterface,
                                Version = version,
                                Driver = exec
                            });
                        } else {
                            // Duplicate/re-broadcast DRIVER_INFO for an already-known device.
                            exec = _discoveredDevices[t.DeviceName].Driver;
                        }

                        // Release tcs (moved outside to handle duplicate DRIVER_INFO messages) —
                        // but only if this DRIVER_INFO actually belongs to the driver LoadDriver
                        // is waiting on. Without this check, a re-broadcast from an unrelated,
                        // already-loaded driver (e.g. a second connected device) could complete
                        // a DIFFERENT in-flight LoadDriver prematurely.
                        if (_driverTcs != null && !_driverTcs.Task.IsCompleted && exec == _pendingLoadDriverExec) {
                            _driverTcs.SetResult(true);
                            Logger.Debug($"LoadDriver operation completed for {t.DeviceName}");
                        }
                    }
                }
            }
        }

        #region Global property store

        // Must be called while holding _lock.
        private void StoreProperty(INDIProperty p) {
            if (p == null || string.IsNullOrEmpty(p.DeviceName)) {
                return;
            }
            if (!_allProperties.TryGetValue(p.DeviceName, out var dict)) {
                dict = [];
                _allProperties[p.DeviceName] = dict;
            }
            dict[p.Name] = p;
        }

        // Must be called while holding _lock.
        private INDIProperty GetStoredProperty(string device, string name) {
            if (!string.IsNullOrEmpty(device)
                && _allProperties.TryGetValue(device, out var dict)
                && dict.TryGetValue(name, out var p)) {
                return p;
            }
            return null;
        }

        /// <summary>
        /// Returns a snapshot of every device currently visible to indiserver together with all of
        /// its known properties. Built under lock so the result is safe to serialize off-thread.
        /// Pass <paramref name="deviceFilter"/> to limit the result to a single device.
        /// </summary>
        public List<INDIDeviceSnapshot> GetDeviceSnapshots(string deviceFilter = null) {
            var result = new List<INDIDeviceSnapshot>();

            // Copy driver metadata first (guarded by its own lock).
            var driverInfo = new Dictionary<string, INDIDeviceInfo>();
            lock (_driverLock) {
                foreach (var kvp in _discoveredDevices) {
                    driverInfo[kvp.Key] = kvp.Value;
                }
            }

            lock (_lock) {
                foreach (var (device, props) in _allProperties) {
                    if (!string.IsNullOrEmpty(deviceFilter) && device != deviceFilter) {
                        continue;
                    }

                    driverInfo.TryGetValue(device, out var info);
                    var snapshot = new INDIDeviceSnapshot {
                        Device = device,
                        DriverExec = info?.Driver ?? string.Empty,
                        DriverName = info?.Name ?? string.Empty,
                        Version = info?.Version ?? string.Empty,
                        Interface = (int)(info?.Interface ?? 0),
                        Connected = IsDeviceConnected(props)
                    };

                    foreach (var prop in props.Values) {
                        snapshot.Properties.Add(BuildPropertySnapshot(prop));
                    }

                    result.Add(snapshot);
                }
            }

            return result;
        }

        // Derives the connection state from the CONNECTION switch vector (CONNECT=On). Caller holds _lock.
        private static bool IsDeviceConnected(Dictionary<string, INDIProperty> props) {
            if (props.TryGetValue("CONNECTION", out var p) && p is INDISwitchProperty sw) {
                return sw.Switches.FirstOrDefault(s => s.Name == "CONNECT")?.Value == true;
            }
            return false;
        }

        // Caller holds _lock.
        private static INDIPropertySnapshot BuildPropertySnapshot(INDIProperty prop) {
            var snap = new INDIPropertySnapshot {
                Device = prop.DeviceName,
                Name = prop.Name,
                Label = prop.Label,
                Group = prop.Group,
                State = prop.State.ToString(),
                Permission = prop.Permission.ToString(),
                Timestamp = prop.Timestamp
            };

            switch (prop) {
                case INDINumberProperty np:
                    snap.Type = "number";
                    foreach (var n in np.Numbers) {
                        snap.Elements.Add(new INDIElementSnapshot {
                            Name = n.Name, Label = n.Label, Value = n.Value,
                            Min = n.Min, Max = n.Max, Step = n.Step, Format = n.Format
                        });
                    }
                    break;
                case INDISwitchProperty swp:
                    snap.Type = "switch";
                    snap.Rule = swp.Rule.ToString();
                    foreach (var s in swp.Switches) {
                        snap.Elements.Add(new INDIElementSnapshot { Name = s.Name, Label = s.Label, Value = s.Value });
                    }
                    break;
                case INDITextProperty tp:
                    snap.Type = "text";
                    foreach (var t in tp.Texts) {
                        snap.Elements.Add(new INDIElementSnapshot { Name = t.Name, Label = t.Label, Value = t.Value });
                    }
                    break;
                case INDILightProperty lp:
                    snap.Type = "light";
                    foreach (var l in lp.Lights) {
                        snap.Elements.Add(new INDIElementSnapshot { Name = l.Name, Label = l.Label, Value = l.State.ToString() });
                    }
                    break;
                case INDIBlobProperty bp:
                    snap.Type = "blob";
                    foreach (var b in bp.Blobs) {
                        snap.Elements.Add(new INDIElementSnapshot { Name = b.Name, Label = b.Label, Format = b.Format });
                    }
                    break;
            }

            return snap;
        }

        /// <summary>
        /// Generic write to any writable property of any device. Looks the property up in the global
        /// store, applies the requested element values (honoring switch rules), and sends the update
        /// to the INDI server. Returns false with a reason when the property is missing, read-only or
        /// the supplied values are invalid.
        /// </summary>
        public bool SetProperty(string device, string name, IDictionary<string, object> elements, out string error) {
            error = null;
            INDIProperty prop;
            lock (_lock) {
                prop = GetStoredProperty(device, name);
            }

            if (prop == null) {
                error = $"Property '{name}' not found on device '{device}'";
                return false;
            }
            if (prop.Permission == PropertyPermission.ReadOnly) {
                error = $"Property '{name}' on device '{device}' is read-only";
                return false;
            }
            if (elements == null || elements.Count == 0) {
                error = "No element values supplied";
                return false;
            }

            try {
                lock (_lock) {
                    switch (prop) {
                        case INDINumberProperty np:
                            foreach (var (elementName, value) in elements) {
                                var n = np.Numbers.FirstOrDefault(x => x.Name == elementName)
                                    ?? throw new ArgumentException($"Number element '{elementName}' not found");
                                n.Value = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                            }
                            break;
                        case INDITextProperty tp:
                            foreach (var (elementName, value) in elements) {
                                var t = tp.Texts.FirstOrDefault(x => x.Name == elementName)
                                    ?? throw new ArgumentException($"Text element '{elementName}' not found");
                                t.Value = value?.ToString() ?? string.Empty;
                            }
                            break;
                        case INDISwitchProperty sp:
                            ApplySwitchValues(sp, elements);
                            break;
                        default:
                            throw new ArgumentException($"Property '{name}' of type '{prop.GetType().Name}' is not writable");
                    }
                }
            } catch (Exception ex) {
                error = ex.Message;
                return false;
            }

            // Send outside the lock; SendProperty serializes the (now-mutated) property to XML.
            SendProperty(prop);
            return true;
        }

        // Applies switch element values honoring the vector's rule. Caller holds _lock.
        private static void ApplySwitchValues(INDISwitchProperty sp, IDictionary<string, object> elements) {
            bool ToBool(object v) => v switch {
                bool b => b,
                string s => s.Trim().ToLowerInvariant() is "on" or "true" or "1",
                _ => Convert.ToBoolean(v, CultureInfo.InvariantCulture)
            };

            // Validate element names up front.
            foreach (var name in elements.Keys) {
                if (sp.Switches.All(s => s.Name != name)) {
                    throw new ArgumentException($"Switch element '{name}' not found");
                }
            }

            // Enforce the vector's rule before mutating anything: multiple ons would make the
            // radio branch below enable them all, and a OneOfMany request with no on element
            // would fall into the else branch and could switch the active element off,
            // producing an illegal all-off vector.
            var onCount = elements.Values.Count(ToBool);
            if (sp.Rule == SwitchRule.OneOfMany && onCount != 1) {
                throw new ArgumentException($"Rule OneOfMany requires exactly one element to be on, got {onCount}");
            }
            if (sp.Rule == SwitchRule.AtMostOne && onCount > 1) {
                throw new ArgumentException($"Rule AtMostOne allows at most one element to be on, got {onCount}");
            }

            bool turningOn = elements.Values.Any(ToBool);

            if ((sp.Rule == SwitchRule.OneOfMany || sp.Rule == SwitchRule.AtMostOne) && turningOn) {
                // Radio behavior: only the explicitly enabled element stays on.
                var enabled = elements.Where(kvp => ToBool(kvp.Value)).Select(kvp => kvp.Key).ToHashSet();
                foreach (var s in sp.Switches) {
                    s.Value = enabled.Contains(s.Name);
                }
            } else {
                foreach (var (elementName, value) in elements) {
                    var s = sp.Switches.First(x => x.Name == elementName);
                    s.Value = ToBool(value);
                }
            }
        }

        /// <summary>
        /// Returns the most recent human-readable INDI messages (driver/server log lines), oldest
        /// first. Pass <paramref name="limit"/> to cap how many of the newest entries are returned.
        /// </summary>
        public List<INDIMessage> GetMessages(int limit = 200) {
            lock (_messagesLock) {
                if (limit <= 0 || _messages.Count <= limit) {
                    return new List<INDIMessage>(_messages);
                }
                return _messages.GetRange(_messages.Count - limit, limit);
            }
        }

        #endregion

        #region XML

        // Appends the decoded chunk, dropping characters that are invalid in XML. The clean
        // scan-then-bulk-append covers the overwhelmingly common case (INDI traffic is ASCII
        // XML + base64 BLOB data) without per-character appends. Surrogate pairs must be
        // handled as pairs: XmlConvert.IsXmlChar is false for each half of a VALID pair, so a
        // naive per-char filter would mangle supplementary characters in device labels. The
        // UTF-8 decoder never splits a pair across chunks (a 4-byte sequence decodes
        // atomically), so pair-wise inspection within one chunk is sufficient.
        private static void AppendValidXmlChars(StringBuilder target, char[] chars, int count) {
            var clean = true;
            for (int i = 0; i < count; i++) {
                var c = chars[i];
                if (char.IsSurrogate(c) || !XmlConvert.IsXmlChar(c)) {
                    clean = false;
                    break;
                }
            }
            if (clean) {
                target.Append(chars, 0, count);
                return;
            }

            for (int i = 0; i < count; i++) {
                var c = chars[i];
                if (char.IsHighSurrogate(c) && i + 1 < count && XmlConvert.IsXmlSurrogatePair(chars[i + 1], c)) {
                    target.Append(c).Append(chars[i + 1]);
                    i++;
                } else if (!char.IsSurrogate(c) && XmlConvert.IsXmlChar(c)) {
                    target.Append(c);
                }
            }
        }

        private void ProcessXmlMessage(StringBuilder message) {
            if (message.Length == 0) return;

            // ── Fast path: waiting for the end of a large element (e.g. setBLOBVector ──────
            // containing a multi-megabyte BLOB). Each call converts only the newly-arrived
            // tail of the buffer instead of the entire thing, turning O(n²) into O(n).
            if (_pendingBigTagName != null) {
                var endTag = "</" + _pendingBigTagName + ">";
                var bufLen = message.Length;
                var from = _pendingBigTagSearchFrom;

                if (bufLen - from <= 0) return;

                var tail = message.ToString(from, bufLen - from);
                var endInTail = tail.IndexOf(endTag, StringComparison.Ordinal);

                if (endInTail == -1) {
                    // Still incomplete — advance the search pointer so the next call
                    // only scans the new bytes (minus a small overlap for boundary cases).
                    _pendingBigTagSearchFrom = Math.Max(0, bufLen - endTag.Length);
                    return;
                }

                var elementLength = from + endInTail + endTag.Length;
                var xmlText = message.ToString(0, elementLength);
                message.Remove(0, elementLength);
                _pendingBigTagName = null;
                _pendingBigTagSearchFrom = 0;

                // Give the BLOB-sized capacity back: chars are 2 bytes, so a 32 MB base64
                // payload would otherwise keep ~85 MB pinned in this builder for the rest
                // of the session — significant on Raspberry Pi class hardware.
                if (message.Capacity > 4 * 1024 * 1024 && message.Length < 64 * 1024) {
                    message.Capacity = Math.Max(message.Length, 64 * 1024);
                }

                // The scanner only hands over elements whose end tag was found, so any parse
                // failure here is genuine corruption worth logging. (An earlier version
                // filtered by exception message text, which is locale-dependent and silently
                // swallowed real errors on non-English .NET locales.)
                try { ProcessElement(XElement.Parse(xmlText)); } catch (Exception ex) {
                    Logger.Error($"Error processing large element: {ex.Message}");
                }

                if (message.Length > 0) ProcessXmlMessage(message);
                return;
            }

            // ── Normal path: parse small/complete elements ────────────────────────────────
            var xmlString = message.ToString();
            if (string.IsNullOrEmpty(xmlString)) return;

            int lastProcessed = 0;
            var elementsToProcess = new List<string>();
            string pendingTagName = null;
            int pendingFrom = 0;

            while (lastProcessed < xmlString.Length) {
                var startTag = xmlString.IndexOf('<', lastProcessed);
                if (startTag == -1) { lastProcessed = xmlString.Length; break; }

                var tagNameEnd = -1;
                for (int i = startTag + 1; i < xmlString.Length; i++) {
                    var c = xmlString[i];
                    if (c == ' ' || c == '>' || c == '/') { tagNameEnd = i; break; }
                }
                if (tagNameEnd == -1) break;

                var tagNameLength = tagNameEnd - startTag - 1;
                if (tagNameLength <= 0) { lastProcessed = startTag + 1; continue; }

                var tagName = xmlString.Substring(startTag + 1, tagNameLength);

                // Find the true end of this opening/self-closing tag, tracking quoted attribute
                // values so a literal "/>" inside one (e.g. a driver <message message="…/> …"/>)
                // isn't mistaken for the tag's own terminator and doesn't truncate the element.
                var tagCloseEnd = -1;
                var isSelfClosing = false;
                char? quoteChar = null;
                for (int i = tagNameEnd; i < xmlString.Length; i++) {
                    var c = xmlString[i];
                    if (quoteChar != null) {
                        if (c == quoteChar) quoteChar = null;
                        continue;
                    }
                    if (c == '"' || c == '\'') { quoteChar = c; continue; }
                    if (c == '>') {
                        tagCloseEnd = i;
                        isSelfClosing = i > 0 && xmlString[i - 1] == '/';
                        break;
                    }
                }

                if (isSelfClosing) {
                    elementsToProcess.Add(xmlString.Substring(startTag, tagCloseEnd + 1 - startTag));
                    lastProcessed = tagCloseEnd + 1;
                    continue;
                }

                if (tagCloseEnd == -1) {
                    // The opening tag itself is cut off at the buffer end (a TCP read boundary
                    // landed inside the tag). Do NOT enter big-tag mode here: the element may
                    // turn out to be self-closing (<message/>, <delProperty/>), whose closing
                    // tag would never arrive and would stall the receive stream forever. Leave
                    // the fragment in the buffer and re-scan once more data has arrived.
                    lastProcessed = startTag;
                    break;
                }

                var endTagStr = "</" + tagName + ">";
                var endIndex = xmlString.IndexOf(endTagStr, startTag + tagNameLength, StringComparison.Ordinal);

                if (endIndex == -1) {
                    // Large incomplete element — switch to fast path for subsequent reads.
                    lastProcessed = startTag;
                    pendingTagName = tagName;
                    pendingFrom = Math.Max(0, xmlString.Length - startTag - endTagStr.Length);
                    break;
                }

                var elementEnd = endIndex + endTagStr.Length;
                elementsToProcess.Add(xmlString.Substring(startTag, elementEnd - startTag));
                lastProcessed = elementEnd;
            }

            if (lastProcessed > 0) message.Remove(0, lastProcessed);

            if (pendingTagName != null) {
                _pendingBigTagName = pendingTagName;
                _pendingBigTagSearchFrom = pendingFrom;
            }

            if (elementsToProcess.Count > 0) {
                // Must stay sequential: INDI is a stateful, ordered stream (e.g. a defXxxVector
                // must be applied before the setXxxVector that follows it in the same batch).
                // The scanner only emits elements whose closing tag was found, so parse errors
                // here are genuine corruption and always logged (no filtering on the exception
                // message — that text is locale-dependent).
                foreach (var xmlText in elementsToProcess) {
                    try {
                        ProcessElement(XElement.Parse(xmlText));
                    } catch (Exception ex) {
                        Logger.Error($"Error processing element: {ex.Message}");
                    }
                }
            }
        }

        private void ProcessElement(XElement element) {
            var deviceName = element.Attribute("device")?.Value ?? string.Empty;
            var propertyName = element.Attribute("name")?.Value ?? string.Empty;

            lock (_lock) {
                INDIProperty property;
                switch (element.Name.LocalName) {
                    case "defNumberVector": {
                            property = INDIProtocolParser.ParseDefNumberVector(element);
                            StoreProperty(property);
                            // Broadcast initial property definition to all registered devices with this name
                            if (_registeredDevices.TryGetValue(deviceName, out var defNumDevices)) {
                                foreach (var deviceInstance in defNumDevices) {
                                    deviceInstance.AddProperty(property);
                                    if (property is INDINumberProperty np)
                                        deviceInstance.OnNumberPropertyUpdated(np);
                                }
                            }
                            break;
                        }
                    case "defSwitchVector": {
                            property = INDIProtocolParser.ParseDefSwitchVector(element);
                            StoreProperty(property);
                            // Broadcast initial property definition to all registered devices with this name
                            if (_registeredDevices.TryGetValue(deviceName, out var defSwDevices)) {
                                foreach (var deviceInstance in defSwDevices) {
                                    deviceInstance.AddProperty(property);
                                    if (property is INDISwitchProperty sp)
                                        deviceInstance.OnSwitchPropertyUpdated(sp);
                                }
                            }
                            break;
                        }
                    case "defTextVector": {
                            property = INDIProtocolParser.ParseDefTextVector(element);
                            StoreProperty(property);
                            CheckForNewDevice(property);
                            // Broadcast initial property definition to all registered devices with this name
                            if (_registeredDevices.TryGetValue(deviceName, out var defTxtDevices)) {
                                foreach (var deviceInstance in defTxtDevices) {
                                    deviceInstance.AddProperty(property);
                                    if (property is INDITextProperty tp)
                                        deviceInstance.OnTextPropertyUpdated(tp);
                                }
                            }
                            break;
                        }
                    case "defBLOBVector": {
                            property = INDIProtocolParser.ParseDefBlobVector(element);
                            StoreProperty(property);
                            // Broadcast initial property definition to all registered devices with this name
                            if (_registeredDevices.TryGetValue(deviceName, out var defBlobDevices)) {
                                foreach (var deviceInstance in defBlobDevices) {
                                    deviceInstance.AddProperty(property);
                                    if (property is INDIBlobProperty bp)
                                        deviceInstance.OnBlobPropertyUpdated(bp);
                                }
                            }
                            break;
                        }
                    case "defLightVector": {
                            property = INDIProtocolParser.ParseDefLightVector(element);
                            StoreProperty(property);
                            if (_registeredDevices.TryGetValue(deviceName, out var defLightDevices)) {
                                foreach (var deviceInstance in defLightDevices) {
                                    deviceInstance.AddProperty(property);
                                    if (property is INDILightProperty lp)
                                        deviceInstance.OnLightPropertyUpdated(lp);
                                }
                            }
                            break;
                        }
                    case "setBLOBVector": {
                            // Update the property on the first registered device (all share the same object)
                            // then notify all devices.
                            if (_registeredDevices.TryGetValue(deviceName, out var setBlobDevices) && setBlobDevices.Count > 0) {
                                if (setBlobDevices[0].GetProperty(propertyName) is INDIBlobProperty bp) {
                                    INDIProtocolParser.UpdateBlobProperty(bp, element);
                                    foreach (var deviceInstance in setBlobDevices)
                                        deviceInstance.OnBlobPropertyUpdated(bp);
                                }
                            }
                            break;
                        }
                    case "setLightVector": {
                            // Update the canonical stored property (shared with any registered devices)
                            // then notify them. Updating the store directly means devices NINA never
                            // connected still get live updates for the control panel.
                            if (GetStoredProperty(deviceName, propertyName) is INDILightProperty lp) {
                                INDIProtocolParser.UpdateLightProperty(lp, element);
                                if (_registeredDevices.TryGetValue(deviceName, out var setLightDevices)) {
                                    foreach (var deviceInstance in setLightDevices)
                                        deviceInstance.OnLightPropertyUpdated(lp);
                                }
                            }
                            break;
                        }
                    case "setTextVector": {
                            if (GetStoredProperty(deviceName, propertyName) is INDITextProperty tp) {
                                INDIProtocolParser.UpdateTextProperty(tp, element);
                                if (_registeredDevices.TryGetValue(deviceName, out var setTxtDevices)) {
                                    foreach (var deviceInstance in setTxtDevices)
                                        deviceInstance.OnTextPropertyUpdated(tp);
                                }
                            }
                            break;
                        }
                    case "setNumberVector": {
                            if (GetStoredProperty(deviceName, propertyName) is INDINumberProperty np) {
                                INDIProtocolParser.UpdateNumberProperty(np, element);
                                if (_registeredDevices.TryGetValue(deviceName, out var setNumDevices)) {
                                    foreach (var deviceInstance in setNumDevices)
                                        deviceInstance.OnNumberPropertyUpdated(np);
                                }
                            }
                            break;
                        }
                    case "setSwitchVector": {
                            if (GetStoredProperty(deviceName, propertyName) is INDISwitchProperty sp) {
                                INDIProtocolParser.UpdateSwitchProperty(sp, element);
                                if (_registeredDevices.TryGetValue(deviceName, out var setSwDevices)) {
                                    foreach (var deviceInstance in setSwDevices)
                                        deviceInstance.OnSwitchPropertyUpdated(sp);
                                }
                            }
                            break;
                        }
                    case "delProperty": {
                            // Per the INDI spec, a delProperty without a name attribute deletes the
                            // ENTIRE device (e.g. a hot-unplugged camera, or a driver shutting down).
                            // Drop it from discovery so rescans don't keep listing stale devices.
                            if (string.IsNullOrEmpty(propertyName)) {
                                lock (_driverLock) {
                                    if (_discoveredDevices.Remove(deviceName)) {
                                        Logger.Info($"Device '{deviceName}' was deleted by its driver - removed from discovered devices");
                                    }
                                }
                                _allProperties.Remove(deviceName);
                                if (_registeredDevices.TryGetValue(deviceName, out var deletedDevices)) {
                                    foreach (var deviceInstance in deletedDevices)
                                        deviceInstance.RemoveAllProperties();
                                }
                            } else {
                                if (_allProperties.TryGetValue(deviceName, out var storedProps)) {
                                    storedProps.Remove(propertyName);
                                }
                                if (_registeredDevices.TryGetValue(deviceName, out var delDevices)) {
                                    foreach (var deviceInstance in delDevices)
                                        deviceInstance.RemoveProperty(propertyName);
                                }
                            }
                            break;
                        }
                    case "message": {
                            var message = element.Attribute("message")?.Value ?? element.Value;
                            // DIAGNOSTIC (rejected-coordinates investigation): surface driver messages
                            // at Info. A genuine slew refusal (e.g. 10micron below horizon / past limits)
                            // is accompanied by a message here; a phantom rejection will not be.
                            Logger.Info($"[INDI Message][{deviceName}] {message}");

                            // Also retain it in the rolling buffer for the control panel log window.
                            if (!string.IsNullOrWhiteSpace(message)) {
                                var ts = element.Attribute("timestamp")?.Value;
                                lock (_messagesLock) {
                                    _messages.Add(new INDIMessage {
                                        Timestamp = string.IsNullOrEmpty(ts) ? DateTime.UtcNow.ToString("o") : ts,
                                        Device = deviceName,
                                        Message = message
                                    });
                                    if (_messages.Count > MaxMessages) {
                                        _messages.RemoveRange(0, _messages.Count - MaxMessages);
                                    }
                                }
                            }
                            break;
                        }
                }
            }
        }

        #endregion

        #region Server

        private Process _process;
        // Cached server info (read from the indiserver stdout or process metadata)
        private string _serverVersionString = string.Empty;
        private Version _serverPlatformVersion = null;
        private const string _fifoPath = "/tmp/indiFIFO";

        private async Task StartServerInFifoMode() {
            try {
                // Kill any existing indiserver processes
                KillExistingServer();

                // First create the FIFO (named pipe) if it doesnt exist
                CreateFifo(_fifoPath);

                var startInfo = new ProcessStartInfo {
                    FileName = "indiserver",
                    Arguments = $"-v -p {_port} -m 1000 -f {_fifoPath}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                var process = Process.Start(startInfo);
                if (process != null) {
                    Logger.Info($"INDI server started in FIFO mode with PID {process.Id}, port: {_port}, using FIFO: {_fifoPath}");

                    // Store the process reference for cleanup on app exit
                    _process = process;

                    // indiserver -v forwards every driver's stderr too, so this can be very
                    // chatty. Drain both streams regardless: if nobody reads them, the ~64 KB
                    // pipe buffer fills and indiserver blocks on write(), hanging the whole stack.
                    process.OutputDataReceived += (s, e) => {
                        if (!string.IsNullOrEmpty(e.Data)) Logger.Debug($"[indiserver] {e.Data}");
                    };
                    process.ErrorDataReceived += (s, e) => {
                        if (!string.IsNullOrEmpty(e.Data)) Logger.Debug($"[indiserver] {e.Data}");
                    };
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                } else {
                    Logger.Error("Failed to start INDI server");
                    _serverReadyTcs.SetResult(false);
                    return;
                }

                // Wait for server to be ready with retries. Connect THIS instance — going
                // through INDIClient.Instance would lazily construct the 7624 singleton
                // (spawning a second server start) if this instance was created directly.
                bool connected = false;
                for (int attempt = 1; attempt <= 10; attempt++) {
                    await Task.Delay(100 * attempt); // Exponential backoff: 100ms, 200ms, 300ms...

                    if (await Connect()) {
                        connected = true;
                        Logger.Info($"Connected to INDI server on attempt {attempt}");
                        break;
                    }

                    Logger.Debug($"Connection attempt {attempt} failed, retrying...");
                }

                if (!connected) {
                    Logger.Error("Could not connect to INDI server after 10 attempts");
                    _serverReadyTcs.SetResult(false);
                    return;
                }

                // Server is ready!
                _serverReadyTcs.SetResult(true);
            } catch (Exception ex) {
                Logger.Error($"Error to start INDI server: {ex.Message}");
                _serverReadyTcs.SetResult(false);
            }
        }

        private void CreateFifo(string fifoPath) {
            try {
                // Check if FIFO already exists
                if (File.Exists(fifoPath)) {
                    Logger.Info($"FIFO already exists: {fifoPath}");
                    return;
                }

                // Create FIFO using mkfifo command
                var mkfifoStartInfo = new ProcessStartInfo {
                    FileName = "mkfifo",
                    Arguments = fifoPath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var mkfifoProcess = Process.Start(mkfifoStartInfo);
                if (mkfifoProcess != null) {
                    mkfifoProcess.WaitForExit();
                    if (mkfifoProcess.ExitCode == 0) {
                        Logger.Info($"Successfully created FIFO: {fifoPath}");
                    } else {
                        Logger.Error($"mkfifo failed with exit code: {mkfifoProcess.ExitCode}");
                    }
                } else {
                    Logger.Error("Failed to start mkfifo process");

                }
            } catch (Exception ex) {
                Logger.Error($"Error creating FIFO {fifoPath}: {ex.Message}");
            }
        }

        private void CleanupServer() {
            // Disconnect will check if process is alive and skip graceful cleanup if not
            if (IsConnected) {
                Disconnect();
            }

            try {
                if (_process != null && !_process.HasExited) {
                    Logger.Info("Shutting down INDI server...");
                    _process.Kill();
                    _process.WaitForExit(5000);
                    _process.Dispose();
                    _process = null;
                    Logger.Info("INDI server shutdown complete");
                }

                // Clean up the FIFO
                if (File.Exists(_fifoPath)) {
                    try {
                        File.Delete(_fifoPath);
                        Logger.Info($"Cleaned up FIFO: {_fifoPath}");
                    } catch (Exception fifoEx) {
                        Logger.Warning($"Failed to delete FIFO {_fifoPath}: {fifoEx.Message}");
                    }
                }
            } catch (Exception ex) {
                Logger.Error($"Error shutting down INDI server: {ex.Message}");
            }
        }

        private void KillExistingServer() {
            try {
                // Use pkill to kill all indiserver processes
                var pkillStartInfo = new ProcessStartInfo {
                    FileName = "pkill",
                    Arguments = "-9 indiserver",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using (var pkillProcess = Process.Start(pkillStartInfo)) {
                    if (pkillProcess != null) {
                        pkillProcess.WaitForExit();
                        if (pkillProcess.ExitCode == 0) {
                            Logger.Info("Killed existing indiserver processes");
                        } else if (pkillProcess.ExitCode == 1) {
                            // Exit code 1 means no processes found, which is fine
                            Logger.Debug("No existing indiserver processes found");
                        } else {
                            Logger.Warning($"pkill returned exit code: {pkillProcess.ExitCode}");
                        }
                    }
                }
            } catch (Exception ex) {
                Logger.Error($"Error killing existing indiserver processes: {ex.Message}");
            }
        }

        #endregion
    }
}
