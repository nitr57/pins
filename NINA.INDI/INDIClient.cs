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
using NINA.Core.Utility.Notification;
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

        // Signals when the server is ready for driver operations. Replaced by a fresh one while the
        // connection to a lost server is being recovered, so waiters see the new server's readiness.
        private volatile TaskCompletionSource<bool> _serverReadyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Set by Dispose: the server and its connection then go away on purpose and are not recovered.
        private volatile bool _shuttingDown;

        // 1 while the server is started or a lost connection is recovered, so a dead socket and the
        // server's exit, which usually arrive together, start one recovery. FinishRecovery checks again
        // for a loss that happened meanwhile.
        private int _recovering;

        // Recoveries of a lost connection, to give up on a server that keeps stopping instead of looping.
        // Only touched with _recovering held.
        private readonly Queue<DateTime> _recoveries = new();
        private const int MaxRecoveries = 3;
        private static readonly TimeSpan RecoveryWindow = TimeSpan.FromMinutes(10);

        // Only a server that was ready once is recovered; a failed start stays a start error.
        private volatile bool _everReady;

        // Set when the client gave up recovering, so later reports of the same loss stay quiet.
        private volatile bool _gaveUp;

        // The session whose main receive loop ended, set before the loss is reported. FinishRecovery finds a
        // loss that was turned away during a recovery through it: the loop's task itself only completes after
        // that report has returned.
        private volatile CancellationTokenSource _endedSession;

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
        // driver last requested for that category, whether this request loaded it or another
        // category already had. Eviction during a rescan only touches the driver previously used
        // by the SAME category, and only when no other category still uses it, so two categories
        // never evict each other's drivers: neither when they share an INDI interface flag (e.g.
        // WEATHER_INTERFACE) nor when they share a driver (e.g. mount and focuser on indi_lx200_OnStep).
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
        private INDIClient(int port) : this(port, startServer: true) {
        }

        /// <summary>
        /// Tests only: <paramref name="startServer"/> false leaves the machine's indiserver alone, and
        /// the test attaches the client to its own fake server with <see cref="Connect"/>.
        /// </summary>
        internal INDIClient(int port, bool startServer) {
            if (port < 1 || port > 65535) {
                throw new ArgumentOutOfRangeException(nameof(port), "Port must be between 1 and 65535.");
            }

            _port = port;
            _managesServer = startServer;
            if (startServer) {
                // Held like a recovery, so a server that dies during its start doesn't start a second one alongside
                _recovering = 1;
                Task.Run(async () => {
                    var ready = false;
                    try {
                        ready = await StartServerInFifoMode();
                    } finally {
                        _everReady = ready;
                        _serverReadyTcs.TrySetResult(ready);
                        FinishRecovery(ready);
                    }
                });
            } else {
                _everReady = true;
                _serverReadyTcs.SetResult(true);
            }
        }

        // False for a test client: Dispose must then leave the machine's indiserver and FIFO alone.
        private readonly bool _managesServer;

        /// <summary>
        /// Tests only: receives the driver start/stop commands of a client created with startServer false,
        /// which never writes to the machine's FIFO.
        /// </summary>
        internal Action<string> FifoCommandsForTests { get; set; }

        /// <summary>
        /// Tests only: makes <see cref="Instance"/>, which every INDIDevice talks to, return this client.
        /// Returns the previous instance to restore afterwards. Never restore null: the next access to
        /// <see cref="Instance"/> would then create a real client, which kills the machine's indiserver.
        /// </summary>
        internal static INDIClient SetInstanceForTests(INDIClient client) {
            ArgumentNullException.ThrowIfNull(client);
            lock (_lock) {
                var previous = _instance;
                _instance = client;
                return previous;
            }
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
                var session = _cts;
                var token = session.Token;
                _receiveTask = Task.Run(async () => {
                    // A lost session is cancelled first, so it applies no element it still has buffered
                    await ReceiveLoop(stream, token, xml => {
                        if (!token.IsCancellationRequested) {
                            ProcessMainElement(xml, token);
                        }
                    }, "main connection");
                    _endedSession = session;
                    OnMainConnectionEnded(session);
                });

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
                // The main connection can die on its own while image connections are still open.
                CloseBlobConnections();
                Logger.Warning("INDI not connected to server");
                return;
            }

            try {
                // Unload drivers - devices will see IsConnected=false and skip waiting
                // Only try to gracefully unload drivers if server process is still alive
                if (IsServerProcessRunning()) {
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
                // applying elements it already read, and a Connect issued right after this
                // Disconnect must not race those writes to the shared stores.
                var receiveTask = _receiveTask;
                _receiveTask = null;
                if (receiveTask != null && !receiveTask.IsCompleted) {
                    try {
                        receiveTask.Wait(TimeSpan.FromSeconds(2));
                    } catch (AggregateException) {
                        // Faulted or cancelled — either way it has exited, which is all we need.
                    }
                }

                CloseBlobConnections();

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

            List<string> devicesToRemove;
            lock (_driverLock) {
                _loadedDrivers.Remove(driverName);

                // Remove devices associated with this driver
                devicesToRemove = _discoveredDevices.Where(d => d.Value.Driver == driverName).Select(d => d.Key).ToList();
                foreach (var deviceKey in devicesToRemove) {
                    _discoveredDevices.Remove(deviceKey);
                    Logger.Debug($"Removed device '{deviceKey}' (driver: {driverName})");
                }
            }
            // Their devices are gone, so their image connections would only idle until Disconnect.
            CloseBlobConnectionsOf(devicesToRemove);
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
            if (!_managesServer) {
                // A test client never writes to the machine's FIFO, which may belong to a running indiserver.
                FifoCommandsForTests?.Invoke(command);
                return true;
            }

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

        // Every camera receives its images on a connection of its own (enableBLOB Only), so the
        // main connection carries the control traffic alone: guide pulse states, exposure states
        // and mount positions never wait behind a multi-megabyte image, and one camera's image
        // never waits behind another camera's. indiserver keeps the BLOB mode per connection, and
        // naming the device in enableBLOB registers the connection's interest in that device, so
        // that single message is the whole handshake. The main connection keeps indiserver's
        // default (Never). Guarded by _blobConnectionsLock.
        private sealed class BlobConnection {
            public string Device;
            public TcpClient Tcp;
            public CancellationTokenSource Cts;
            public DateTime OpenedAt;
            public Task Loop;
        }

        private readonly Dictionary<string, BlobConnection> _blobConnections = [];
        private readonly object _blobConnectionsLock = new();
        private static readonly TimeSpan BlobConnectTimeout = TimeSpan.FromSeconds(5);
        // An image connection the server closes sooner than this after opening is not reopened
        // (the camera falls back to the main connection) so a server that keeps refusing it can't
        // make pins reconnect in a tight loop. Settable for tests only.
        internal TimeSpan BlobConnectionMinLifetime { get; set; } = TimeSpan.FromSeconds(5);

        public void EnableBLOB(string deviceName) {
            if (!IsConnected) {
                return;
            }

            bool opened;
            lock (_blobConnectionsLock) {
                if (_blobConnections.ContainsKey(deviceName)) {
                    return;
                }
                opened = OpenBlobConnection(deviceName);
            }

            if (!opened) {
                FallBackToMainConnection(deviceName);
            }
        }

        // The camera still works, but whatever follows one of its images on the main connection waits for it.
        private void FallBackToMainConnection(string deviceName) {
            SendMessage(new XElement("enableBLOB", new XAttribute("device", deviceName), "Also"));
            Logger.Warning($"INDI: receiving the images of '{deviceName}' on the main connection, so other updates can wait behind them");
        }

        // Caller holds _blobConnectionsLock.
        private bool OpenBlobConnection(string deviceName) {
            var tcp = new TcpClient();
            try {
                using (var timeout = new CancellationTokenSource(BlobConnectTimeout)) {
                    tcp.ConnectAsync("localhost", _port, timeout.Token).AsTask().GetAwaiter().GetResult();
                }
                var stream = tcp.GetStream();
                var handshake = Encoding.UTF8.GetBytes(
                    new XElement("enableBLOB", new XAttribute("device", deviceName), "Only").ToString(SaveOptions.DisableFormatting));
                stream.Write(handshake, 0, handshake.Length);
                stream.Flush();

                var connection = new BlobConnection {
                    Device = deviceName,
                    Tcp = tcp,
                    Cts = new CancellationTokenSource(),
                    OpenedAt = DateTime.UtcNow
                };
                var token = connection.Cts.Token;
                _blobConnections[deviceName] = connection;
                connection.Loop = Task.Run(async () => {
                    await ReceiveLoop(stream, token, ProcessBlobElement, $"image connection of '{deviceName}'");
                    OnBlobConnectionEnded(connection);
                });
                Logger.Info($"INDI: receiving the images of '{deviceName}' on a dedicated connection");
                return true;
            } catch (Exception ex) {
                tcp.Dispose();
                Logger.Error($"INDI: could not open an image connection for '{deviceName}': {ex.Message}");
                return false;
            }
        }

        private void OnBlobConnectionEnded(BlobConnection connection) {
            lock (_blobConnectionsLock) {
                if (!_blobConnections.TryGetValue(connection.Device, out var current) || current != connection) {
                    // Closed by Disconnect or a driver unload, which dispose it.
                    return;
                }
                _blobConnections.Remove(connection.Device);
            }
            CloseBlobConnection(connection);

            if (!IsConnected) {
                return;
            }
            // e.g. indiserver dropped it for falling too far behind (-m): images of a camera that is
            // still connected would otherwise never arrive again.
            if (DateTime.UtcNow - connection.OpenedAt < BlobConnectionMinLifetime) {
                FallBackToMainConnection(connection.Device);
            } else {
                Logger.Warning($"INDI: the server closed the image connection of '{connection.Device}', reopening it");
                EnableBLOB(connection.Device);
            }
        }

        private static void CloseBlobConnection(BlobConnection connection) {
            connection.Cts.Cancel();
            connection.Tcp.Close();
            connection.Cts.Dispose();
        }

        private void CloseBlobConnections() {
            List<BlobConnection> connections;
            lock (_blobConnectionsLock) {
                connections = _blobConnections.Values.ToList();
                _blobConnections.Clear();
            }
            CloseAndWait(connections);
        }

        private void CloseBlobConnectionsOf(IEnumerable<string> devices) {
            var connections = new List<BlobConnection>();
            lock (_blobConnectionsLock) {
                foreach (var device in devices) {
                    // Removed first, so OnBlobConnectionEnded neither reopens it nor falls back to the main connection.
                    if (_blobConnections.Remove(device, out var connection)) {
                        connections.Add(connection);
                        Logger.Info($"INDI: closing the image connection of '{device}', its driver was unloaded");
                    }
                }
            }
            CloseAndWait(connections);
        }

        private static void CloseAndWait(List<BlobConnection> connections) {
            foreach (var connection in connections) {
                connection.Cts.Cancel();
                connection.Tcp.Close();
            }
            // Bounded, like the main loop: a loop may be applying an image right now.
            foreach (var connection in connections) {
                try {
                    connection.Loop?.Wait(TimeSpan.FromSeconds(2));
                } catch (AggregateException) {
                    // Faulted or cancelled — either way it has exited.
                }
                connection.Cts.Dispose();
            }
        }

        private void ProcessMainElement(string xmlText, CancellationToken session) {
            ProcessElement(XElement.Parse(xmlText), session);
        }

        private void ProcessBlobElement(string xmlText) {
            // An Only connection carries nothing but setBLOBVector; ignore anything else.
            if (xmlText.StartsWith("<setBLOBVector", StringComparison.Ordinal)) {
                ProcessElement(XElement.Parse(xmlText));
            }
        }

        private bool HasRegisteredBlobProperty(string deviceName, string propertyName) {
            lock (_lock) {
                return _registeredDevices.TryGetValue(deviceName, out var devices) && devices.Count > 0
                    && devices[0].GetProperty(propertyName) is INDIBlobProperty;
            }
        }

        // State of the O(n) large-element fast path in ProcessXmlMessage. One per connection:
        // the main connection and every image connection scan their own stream.
        private sealed class XmlScanState {
            public string PendingBigTagName;
            public int PendingBigTagSearchFrom;
        }

        // The stream is a parameter on purpose: this loop must only ever read the stream it
        // was started with. Re-reading the _stream field would let a loop from a previous
        // session pick up (and corrupt) the NEW session's stream after a fast
        // Disconnect→Connect cycle. <paramref name="handleElement"/> receives every complete
        // top-level element, in wire order.
        private async Task ReceiveLoop(NetworkStream stream, CancellationToken ct, Action<string> handleElement, string connectionName) {
            if (stream == null) return;

            // Fresh per connection, like the xmlBuffer: a previous connection that died
            // mid-large-element must not make this one wait for that element's end tag.
            var scan = new XmlScanState();

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
                        Logger.Error($"INDI server closed the {connectionName}");
                        break;
                    }

                    var charCount = decoder.GetChars(buffer, 0, bytesRead, charBuffer, 0);
                    AppendValidXmlChars(xmlBuffer, charBuffer, charCount);

                    // Process complete XML elements
                    ProcessXmlMessage(xmlBuffer, scan, handleElement);
                } catch (Exception ex) when (ex is not OperationCanceledException) {
                    if (ct.IsCancellationRequested) {
                        // Normal Disconnect: the stream was closed under us, so the read
                        // faulted (ObjectDisposedException etc.) — not a receive error.
                        break;
                    }
                    Logger.Error($"Receive error on the {connectionName}: {ex.Message}");
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
                    // Record the category's driver even when it is already loaded (e.g. started by
                    // another category with the same driver): eviction below must know every
                    // category a driver serves, or a mount and a focuser on indi_lx200_OnStep would
                    // lose their shared driver as soon as either of them switches to another one.
                    // Only GetDevices touches _lastDriverPerCategory, serialized by the semaphore.
                    if (deviceTypeCategory != null) {
                        if (_lastDriverPerCategory.TryGetValue(deviceTypeCategory, out string oldDriver) && oldDriver != driver) {
                            var stillUsedBy = _lastDriverPerCategory
                                .Where(c => c.Key != deviceTypeCategory && c.Value == oldDriver)
                                .Select(c => c.Key)
                                .ToList();
                            if (stillUsedBy.Count > 0) {
                                Logger.Info($"Keeping previous {deviceTypeCategory} driver '{oldDriver}', still used by {string.Join(", ", stillUsedBy)}");
                            } else {
                                Logger.Info($"Evicting previous {deviceTypeCategory} driver '{oldDriver}' before loading '{driver}'");
                                UnloadDriver(oldDriver);
                            }
                        }
                        _lastDriverPerCategory[deviceTypeCategory] = driver;
                    }

                    bool driverAlreadyLoaded;
                    lock (_driverLock) {
                        driverAlreadyLoaded = _loadedDrivers.Contains(driver);
                    }
                    if (!driverAlreadyLoaded) {
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
            // The server and its connection now go away on purpose
            _shuttingDown = true;
            Disconnect();
            if (_managesServer) {
                CleanupServer();
            }
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
                            // Duplicate/re-broadcast DRIVER_INFO for an already-known device. Drivers
                            // widen DRIVER_INTERFACE once they have probed the hardware (syncDriverInfo,
                            // e.g. a focuser that found a light box or OnStep's rotator), so the latest
                            // value wins — otherwise a rescan never sees the added device types.
                            var known = _discoveredDevices[t.DeviceName];
                            exec = known.Driver;

                            var interfaceText = t.Texts.FirstOrDefault(text => text.Name == "DRIVER_INTERFACE");
                            if (int.TryParse(interfaceText?.Value, out var interfaceNumber)
                                && (DeviceInterface)interfaceNumber != known.Interface) {
                                Logger.Info($"INDI device '{t.DeviceName}' changed its interface from {known.Interface} to {(DeviceInterface)interfaceNumber}");
                                known.Interface = (DeviceInterface)interfaceNumber;
                            }
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

        private void ProcessXmlMessage(StringBuilder message, XmlScanState scan, Action<string> handleElement) {
            if (message.Length == 0) return;

            // ── Fast path: waiting for the end of a large element (e.g. setBLOBVector ──────
            // containing a multi-megabyte BLOB). Each call converts only the newly-arrived
            // tail of the buffer instead of the entire thing, turning O(n²) into O(n).
            if (scan.PendingBigTagName != null) {
                var endTag = "</" + scan.PendingBigTagName + ">";
                var bufLen = message.Length;
                var from = scan.PendingBigTagSearchFrom;

                if (bufLen - from <= 0) return;

                var tail = message.ToString(from, bufLen - from);
                var endInTail = tail.IndexOf(endTag, StringComparison.Ordinal);

                if (endInTail == -1) {
                    // Still incomplete — advance the search pointer so the next call
                    // only scans the new bytes (minus a small overlap for boundary cases).
                    scan.PendingBigTagSearchFrom = Math.Max(0, bufLen - endTag.Length);
                    return;
                }

                var elementLength = from + endInTail + endTag.Length;
                var xmlText = message.ToString(0, elementLength);
                message.Remove(0, elementLength);
                scan.PendingBigTagName = null;
                scan.PendingBigTagSearchFrom = 0;

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
                try { handleElement(xmlText); } catch (Exception ex) {
                    Logger.Error($"Error processing large element: {ex.Message}");
                }

                if (message.Length > 0) ProcessXmlMessage(message, scan, handleElement);
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
                scan.PendingBigTagName = pendingTagName;
                scan.PendingBigTagSearchFrom = pendingFrom;
            }

            if (elementsToProcess.Count > 0) {
                // Must stay sequential: INDI is a stateful, ordered stream (e.g. a defXxxVector
                // must be applied before the setXxxVector that follows it in the same batch).
                // The scanner only emits elements whose closing tag was found, so parse errors
                // here are genuine corruption and always logged (no filtering on the exception
                // message — that text is locale-dependent).
                foreach (var xmlText in elementsToProcess) {
                    try {
                        handleElement(xmlText);
                    } catch (Exception ex) {
                        Logger.Error($"Error processing element: {ex.Message}");
                    }
                }
            }
        }

        // <paramref name="session"/>: the main connection's session. Checked again under _lock, since a lost
        // session is torn down under that lock and must not apply an element after its stores were cleared.
        private void ProcessElement(XElement element, CancellationToken session = default) {
            var deviceName = element.Attribute("device")?.Value ?? string.Empty;
            var propertyName = element.Attribute("name")?.Value ?? string.Empty;

            // Decode image payloads before taking _lock: a large camera image can take a noticeable
            // time to decode on a Pi, and the main connection's updates need _lock too.
            List<INDIProtocolParser.DecodedBlob> decodedBlobs = null;
            if (element.Name.LocalName == "setBLOBVector" && HasRegisteredBlobProperty(deviceName, propertyName)) {
                decodedBlobs = INDIProtocolParser.DecodeBlobs(element);
            }

            lock (_lock) {
                if (session.IsCancellationRequested) {
                    return;
                }

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
                                    // null only if the device registered while the payload was decoded
                                    decodedBlobs ??= INDIProtocolParser.DecodeBlobs(element);
                                    INDIProtocolParser.ApplyBlobUpdate(bp, element, decodedBlobs);
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
                                // syncDriverInfo() re-publishes DRIVER_INFO as a set, not a def.
                                CheckForNewDevice(tp);
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

        // Starts indiserver and connects to it; true once connected. The caller completes _serverReadyTcs:
        // the initial start, or a recovery once it has started the drivers again too.
        private async Task<bool> StartServerInFifoMode() {
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

                if (_shuttingDown) {
                    return false;
                }

                var process = Process.Start(startInfo);
                if (process == null) {
                    Logger.Error("Failed to start INDI server");
                    return false;
                }

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

                // Notices a dead server even before its connection reports the end
                process.Exited += (_, _) => OnServerProcessExited(process);
                process.EnableRaisingEvents = true;

                // Dispose ran while the server was starting, so CleanupServer may have missed it
                if (_shuttingDown) {
                    try {
                        process.Kill();
                    } catch (InvalidOperationException) {
                        // Already gone
                    }

                    return false;
                }

                if (!await ConnectWithRetriesAsync(IsServerProcessRunning)) {
                    Logger.Error("Could not connect to INDI server after 10 attempts");
                    return false;
                }

                return true;
            } catch (Exception ex) {
                Logger.Error($"Error to start INDI server: {ex.Message}");
                return false;
            }
        }

        // Wait for server to be ready with retries. Connect THIS instance — going through
        // INDIClient.Instance would lazily construct the 7624 singleton (spawning a second
        // server start) if this instance was created directly. Stops early once
        // <paramref name="serverRunning"/> says the server is gone.
        private async Task<bool> ConnectWithRetriesAsync(Func<bool> serverRunning = null) {
            for (int attempt = 1; attempt <= 10; attempt++) {
                await Task.Delay(100 * attempt); // Exponential backoff: 100ms, 200ms, 300ms...

                if (_shuttingDown || (serverRunning != null && !serverRunning())) {
                    return false;
                }

                if (await Connect()) {
                    Logger.Info($"Connected to INDI server on attempt {attempt}");
                    return true;
                }

                Logger.Debug($"Connection attempt {attempt} failed, retrying...");
            }

            return false;
        }

        private void OnServerProcessExited(Process process) {
            if (!ReferenceEquals(process, _process)) {
                // A server this client already replaced (or killed on purpose while recovering)
                return;
            }

            string exitCode;
            try {
                exitCode = process.ExitCode.ToString(CultureInfo.InvariantCulture);
            } catch (InvalidOperationException) {
                exitCode = "unknown";
            }

            HandleServerConnectionLost($"indiserver exited (exit code {exitCode})");
        }

        // The main receive loop of a session ended. Disconnect cancels the session first, so an
        // uncancelled end means the server closed the connection or the socket failed.
        private void OnMainConnectionEnded(CancellationTokenSource session) {
            if (session.IsCancellationRequested || !ReferenceEquals(session, _cts)) {
                return;
            }

            HandleServerConnectionLost("the connection to indiserver ended");
        }

        /// <summary>
        /// The server died or the main connection to it is gone: no update arrives for any device any
        /// more and commands would vanish. Tears the dead session down, tells every registered device that
        /// it is disconnected (the equipment layer then reports the lost connection and disconnects) and
        /// reconnects to, or restarts, indiserver. Equipment is not reconnected: after the recovery it can
        /// be connected again as usual.
        /// </summary>
        private void HandleServerConnectionLost(string reason, bool announce = true) {
            if (_shuttingDown || _gaveUp) {
                return;
            }

            if (!_everReady) {
                // The server never came up, so there is nothing to recover; the start already reported its failure
                Logger.Error($"INDI: {reason} before the INDI server was ever ready");
                return;
            }

            if (Interlocked.Exchange(ref _recovering, 1) == 1) {
                // During a start or recovery FinishRecovery picks the loss up
                return;
            }

            var recover = RecordRecovery();
            Logger.Error($"INDI: {reason}. Marking all INDI devices disconnected{(recover ? " and recovering the INDI server" : string.Empty)}");

            try {
                // GetDevices and WaitForServerReadyAsync wait for the recovered server from now on; a
                // signal still pending keeps its waiters
                if (_serverReadyTcs.Task.IsCompleted) {
                    _serverReadyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                }

                TearDownLostSession();
            } catch (Exception ex) {
                Logger.Error($"INDI: cleaning up the lost INDI server connection failed: {ex.Message}");
            } finally {
                if (recover) {
                    if (announce) {
                        Notification.ShowError("Lost the connection to the INDI server. Reconnecting to it; connect the INDI equipment again afterwards.");
                    }

                    _ = Task.Run(RecoverServerAsync);
                } else {
                    _gaveUp = true;
                    Logger.Error($"INDI: the INDI server connection was lost {MaxRecoveries} times within {RecoveryWindow.TotalMinutes:F0} minutes; not recovering it again. Restart pins to use INDI devices.");
                    Notification.ShowError("The INDI server keeps stopping, so pins stopped reconnecting to it. Restart pins to use INDI equipment.");
                    _serverReadyTcs.TrySetResult(false);
                    Interlocked.Exchange(ref _recovering, 0);
                }
            }
        }

        // This can run on the receive loop that just ended, so that loop is not waited for
        private void TearDownLostSession() {
            // Cancelled first: the session's receive loop then applies no element it still has buffered
            _cts?.Cancel();
            lock (_operationLock) {
                _stream?.Close();
                _tcpClient?.Close();
                _stream = null;
                _tcpClient = null;
            }
            _receiveTask = null;
            CloseBlobConnections();

            lock (_driverLock) {
                // A LoadDriver waiting for DRIVER_INFO gets no answer from a dead connection
                _driverTcs?.TrySetResult(false);
            }

            // Taking _lock also waits for an element the old session is applying right now
            List<INDIDevice> devices;
            lock (_lock) {
                _allProperties.Clear();
                devices = _registeredDevices.Values.SelectMany(list => list).Distinct().ToList();
            }

            foreach (var device in devices) {
                try {
                    device.OnServerConnectionLost();
                } catch (Exception ex) {
                    Logger.Error($"INDI: failed to mark '{device.DeviceName}' disconnected: {ex.Message}");
                }
            }
        }

        private async Task RecoverServerAsync() {
            var recovered = false;
            try {
                // A running server keeps its drivers loaded, so only the connection is restored. Starting
                // them again would run a second instance of each driver.
                if (!_managesServer || IsServerProcessRunning()) {
                    recovered = await ConnectWithRetriesAsync(_managesServer ? IsServerProcessRunning : null);
                    if (recovered) {
                        Logger.Info("INDI: reconnected to the running INDI server");
                    }
                }

                if (!recovered && _managesServer && !_shuttingDown) {
                    recovered = await RestartServerAsync();
                }

                if (recovered) {
                    Notification.ShowInformation("The INDI server is back. Connect the INDI equipment again.");
                }
            } catch (Exception ex) {
                Logger.Error($"INDI: recovering the INDI server failed: {ex.Message}");
            } finally {
                _serverReadyTcs.TrySetResult(recovered);
                FinishRecovery(recovered);
            }
        }

        // The drivers died with the old server. They are started again under the new one, so connecting the
        // equipment works without a rescan, through LoadDriver, which tracks each one and waits until it has
        // described itself. Readiness is only signalled after that, so a rescan right after it finds the devices.
        // _getDriversSemaphore keeps a GetDevices that passed the readiness check before the loss from starting
        // a driver of its own in between, which would run it twice.
        private async Task<bool> RestartServerAsync() {
            Logger.Warning("INDI: restarting indiserver");

            await _getDriversSemaphore.WaitAsync();
            try {
                Dictionary<string, DeviceInterface> drivers;
                lock (_driverLock) {
                    drivers = _loadedDrivers.ToDictionary(
                        driver => driver,
                        driver => _discoveredDevices.Values.Where(d => d.Driver == driver).Aggregate((DeviceInterface)0, (all, d) => all | d.Interface));
                    _loadedDrivers.Clear();
                    _discoveredDevices.Clear();
                }

                var oldProcess = _process;
                _process = null;
                try {
                    oldProcess?.Dispose();
                } catch (Exception) {
                    // Only releasing the handle of a process that is gone
                }

                if (!await StartServerInFifoMode()) {
                    return false;
                }

                await EndOldDriverProcessesAsync(drivers.Keys);

                foreach (var (driver, deviceInterface) in drivers) {
                    if (await LoadDriver(driver, deviceInterface)) {
                        Logger.Info($"INDI: started driver '{driver}' again");
                    } else {
                        Logger.Error($"INDI: driver '{driver}' did not come back after the restart");
                    }
                }

                // As GetDevices: multi-device drivers announce their further devices shortly after the first
                await Task.Delay(TimeSpan.FromMilliseconds(500));
                return true;
            } finally {
                _getDriversSemaphore.Release();
            }
        }

        // A driver ends when its server is gone (EOF on stdin), but one stuck in an SDK or USB call can outlive
        // it for a moment and still hold its device, which the new instance then cannot open. Waits for them
        // and kills what is left after DriverExitTimeout.
        private static async Task EndOldDriverProcessesAsync(IEnumerable<string> drivers) {
            var remaining = drivers.ToList();
            var deadline = DateTime.UtcNow + DriverExitTimeout;
            while (remaining.Count > 0) {
                remaining = remaining.Where(driver => RunPgrep("-c", driver) > 0).ToList();
                if (remaining.Count == 0 || DateTime.UtcNow > deadline) {
                    break;
                }

                await Task.Delay(200);
            }

            foreach (var driver in remaining) {
                Logger.Warning($"INDI: driver '{driver}' outlived its server, killing it");
                using var pkill = Process.Start(new ProcessStartInfo("pkill", $"-9 -f \"^{driver}( |$)\"") {
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                pkill?.WaitForExit();
            }
        }

        private static readonly TimeSpan DriverExitTimeout = TimeSpan.FromSeconds(5);

        // Matches driver processes by command line: Linux cuts process names to 15 characters, which many
        // INDI driver names share
        private static int RunPgrep(string options, string driver) {
            try {
                var startInfo = new ProcessStartInfo("pgrep", $"{options} -f \"^{driver}( |$)\"") {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true
                };
                using var pgrep = Process.Start(startInfo);
                var output = pgrep!.StandardOutput.ReadToEnd();
                pgrep.WaitForExit();
                return int.TryParse(output.Trim(), out var count) ? count : 0;
            } catch (Exception ex) {
                Logger.Warning($"INDI: could not look for old '{driver}' processes: {ex.Message}");
                return 0;
            }
        }

        // Ends a start or recovery. A loss during it was turned away (one recovery at a time), so check again.
        // After a failed recovery a loss found here is retried without announcing it a second time.
        private void FinishRecovery(bool recovered) {
            // Released before checking, so a loss reported from now on starts a recovery by itself, and one
            // reported before was marked in _endedSession or shows in the process state
            Interlocked.Exchange(ref _recovering, 0);

            if (_shuttingDown || _gaveUp || !_everReady) {
                return;
            }

            var session = _cts;
            if (session != null && ReferenceEquals(_endedSession, session) && !session.IsCancellationRequested) {
                HandleServerConnectionLost("the connection to indiserver ended during the recovery", announce: recovered);
            } else if (_managesServer && _process != null && !IsServerProcessRunning()) {
                HandleServerConnectionLost("indiserver exited during the recovery", announce: recovered);
            } else if (!recovered) {
                Logger.Error("INDI: could not recover the INDI server");
                Notification.ShowError("Could not reconnect to the INDI server. Restart pins to use INDI equipment.");
            }
        }

        private bool IsServerProcessRunning() {
            var process = _process;
            try {
                return process != null && !process.HasExited;
            } catch (InvalidOperationException) {
                return false;
            }
        }

        // False once the connection was recovered MaxRecoveries times within RecoveryWindow
        private bool RecordRecovery() {
            var now = DateTime.UtcNow;
            while (_recoveries.Count > 0 && now - _recoveries.Peek() > RecoveryWindow) {
                _recoveries.Dequeue();
            }

            if (_recoveries.Count >= MaxRecoveries) {
                return false;
            }

            _recoveries.Enqueue(now);
            return true;
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
