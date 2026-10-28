#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Core.Utility.Extensions;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace NINA.Core.Utility.WindowService {

    /// <summary>
    /// A window should be associated to a viewmodel by the DataTemplates.xaml
    /// </summary>
    public class WindowService : IWindowService {
        protected Dispatcher dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        protected CustomWindow window;

        // Track which content objects already have event handlers to prevent duplicates
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<object, bool> _subscribedContent = new();
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<object, PropertyChangedEventHandler> _eventHandlers = new();
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<object, System.Threading.Timer> _debounceTimers = new();

        // Track WindowService instances for debugging
        private static int _instanceCounter = 0;
        private readonly int _instanceId;

        // Track the specific dialog ID registered by this instance (headless mode only)
        private int _currentDialogId = -1;

        public WindowService() {
            _instanceId = System.Threading.Interlocked.Increment(ref _instanceCounter);
        }

        public void Show(object content, string title = "", ResizeMode resizeMode = ResizeMode.NoResize, WindowStyle windowStyle = WindowStyle.None) {
            // Check if running in headless mode
            if (System.Windows.DialogService.IsHeadless()) {
                ShowViaDialogService(content, title);
                return;
            }

            dispatcher.Invoke(DispatcherPriority.Normal, new Action(() => {
                try {
                    window = GenerateWindow(content, title, resizeMode, windowStyle, null);
                    window.Show();
                } catch (Exception ex) {
                    Logger.Error(ex);
                }
            }));
        }

        public void DelayedClose(TimeSpan t) {
            _ = Task.Run(async () => {
                try {
                    await CoreUtil.Wait(t);
                    await this.Close();
                } catch (Exception ex) {
                    Logger.Error(ex);
                }
            });
        }

        public async Task Close() {
            // In headless mode, we need to close via DialogService
            if (System.Windows.DialogService.IsHeadless()) {
                // Close only the specific dialog owned by this WindowService instance
                var ownDialogId = _currentDialogId;
                if (ownDialogId >= 0) {
                    _currentDialogId = -1;
                    System.Windows.DialogService.CloseDialog(ownDialogId, result: true);
                }
                return;
            }

            await dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => {
                try {
                    window?.Close();
                } catch (Exception e) {
                    Logger.Error(e);
                }
            }));
        }

        private CustomWindow GenerateWindow(object content, string title, ResizeMode resizeMode, WindowStyle windowStyle, ICommand closeCommand) {
            var mainwindow = Application.Current.MainWindow;
            var customWindowStyle = Application.Current.TryFindResource("NoResizeWindow") as Style;
            var window = new CustomWindow() {
                SizeToContent = SizeToContent.WidthAndHeight,
                Title = title,
                Background = Application.Current.TryFindResource("BackgroundBrush") as Brush,
                ResizeMode = resizeMode,
                WindowStyle = customWindowStyle != null ? WindowStyle.None : windowStyle,
                Style = customWindowStyle,
                MinHeight = 300,
                MinWidth = 350,
                Owner = mainwindow,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = content
            };

            if (closeCommand == null) {
                window.CloseCommand = new RelayCommand((object o) => window.Close());
            } else {
                window.CloseCommand = closeCommand;
            }

            window.Closing += (sender, e) => {
                if (sender is Window cw && cw.IsFocused) {
                    try { mainwindow.Focus(); } catch { }
                }
            };
            window.Closed += (sender, e) => {
                try { this.OnClosed?.Invoke(this, EventArgs.Empty); } catch { }
                try { mainwindow.Focus(); } catch { }
            };

            return window;
        }

        public IDispatcherOperationWrapper ShowDialog(object content, string title = "", ResizeMode resizeMode = ResizeMode.NoResize, WindowStyle windowStyle = WindowStyle.None, ICommand closeCommand = null) {
            // Check if running in headless mode
            if (System.Windows.DialogService.IsHeadless()) {
                // Return a wrapper whose Task completes when the DialogService dialog is closed
                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                // Pass a callback so DialogService will set the TCS when dialog is closed
                _ = ShowViaDialogService(content, title, closeCommand, (result) => tcs.TrySetResult(result));

                return new HeadlessDispatcherOperationWrapper(dispatcher, tcs.Task);
            }

            return new DispatcherOperationWrapper(dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => {
                try {
                    window = GenerateWindow(content, title, resizeMode, windowStyle, closeCommand);

                    Application.Current.MainWindow.Opacity = 0.8;
                    try {
                        var result = window.ShowDialog();
                        this.OnDialogResultChanged?.Invoke(this, new DialogResultEventArgs(result));
                    } finally {
                        Application.Current.MainWindow.Opacity = 1;
                    }
                } catch (Exception e) {
                    Logger.Error(e);
                }
            })));
        }

        public event EventHandler OnDialogResultChanged;

        public event EventHandler OnClosed;

        /// <summary>
        /// Clean up event handlers for a specific content object
        /// Called by DialogService when dialog closes
        /// </summary>
        public static void CleanupEventHandlersForContent(object content) {
            if (content == null) return;

            // Remove from subscription tracking FIRST (this stops pending timer callbacks)
            _subscribedContent.TryRemove(content, out _);

            // Stop and dispose the debounce timer if it exists
            if (_debounceTimers.TryRemove(content, out var timer)) {
                try {
                    // Stop the timer from firing (change to Infinite delay)
                    timer?.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
                    timer?.Dispose();
                } catch {
                    // Timer might already be disposed or firing, ignore
                }
            }

            // Unsubscribe the event handler if it exists
            if (_eventHandlers.TryRemove(content, out var handler)) {
                if (content is INotifyPropertyChanged notifyObj) {
                    notifyObj.PropertyChanged -= handler;
                }
            }
        }

        private int ShowViaDialogService(object content, string title, ICommand closeCommand = null, Action<bool> resultCallback = null) {
            try {
                var contentType = content?.GetType().FullName ?? "UnknownWindow";

                // Compose a ResultCallback that invokes both the external callback (if provided)
                // and the closeCommand (if provided)
                Action<bool> composedCallback = (result) => {
                    try { resultCallback?.Invoke(result); } catch { }
                    try {
                        if (closeCommand != null && closeCommand.CanExecute(result)) {
                            closeCommand.Execute(result);
                        }
                    } catch { }
                    // Mirrors the real WPF path's Window.Closed handler (GenerateWindow below): callers that
                    // subscribe to OnClosed to dispose/cancel a dialog VM when dismissed without a recognized
                    // button (e.g. a headless dialog's fallback Cancel, or the DialogService window being
                    // superseded) must still be notified in headless mode, or they hang waiting forever.
                    try { this.OnClosed?.Invoke(this, EventArgs.Empty); } catch { }
                };

                var dialog = new DialogService.DialogInfo {
                    Title = title ?? "Dialog",
                    Message = ExtractMessage(content),
                    ContentType = contentType,
                    DataContext = content,
                    ResultCallback = composedCallback,
                    // See ExtractCommandButtons for what this event signals; DialogService.ClickButton reads this
                    // to decide whether a button click should force-close the dialog.
                    SelfClosing = content?.GetType().GetEvent("RequestClose") != null
                };

                // Extract content properties
                if (content != null) {
                    dialog.Content = ExtractProperties(content);
                }

                int dialogId = DialogService.RegisterDialog(dialog);

                // Track this dialog so Close() only closes this specific one
                _currentDialogId = dialogId;

                // Subscribe to property changes for real-time updates
                // Only subscribe once per content object to prevent duplicate broadcasts
                if (content is INotifyPropertyChanged notifyObj && _subscribedContent.TryAdd(content, true)) {
                    PropertyChangedEventHandler handler = null;
                    handler = (sender, e) => {

                        // Debounce: Reset timer on each property change, only broadcast after 100ms of silence
                        var timer = _debounceTimers.AddOrUpdate(content,
                            // Add new timer
                            key => new System.Threading.Timer(_ => {
                                _ = Task.Run(async () => {
                                    try {
                                        // Check if this content is still subscribed (dialog not closed)
                                        if (!_subscribedContent.ContainsKey(content)) {
                                            return; // Dialog was closed, don't broadcast
                                        }

                                        var broadcaster = SignalR.DialogBroadcaster.Instance;
                                        if (broadcaster != null) {
                                            var parameters = dialog.Content.ToDictionary(
                                                kvp => kvp.Key,
                                                kvp => kvp.Value?.ToString() ?? ""
                                            );

                                            var dialogData = new Model.DialogData {
                                                Title = title ?? "Dialog",
                                                ContentType = dialog.ContentType,
                                                Active = true,
                                                Status = ExtractMessage(content),
                                                Parameters = parameters,
                                                AvailableCommands = new List<string> { "Cancel" }
                                            };

                                            // Special handling for PlateSolvingStatusVM
                                            if (content?.GetType().FullName == "NINA.WPF.Base.ViewModel.PlateSolvingStatusVM") {
                                                dialogData.SlewAndCenter = ExtractSlewAndCenterData(content);
                                            }

                                            // Special handling for MeridianFlipVM
                                            if (content?.GetType().FullName == "NINA.WPF.Base.ViewModel.MeridianFlipVM") {
                                                dialogData.MeridianFlip = ExtractMeridianFlipData(content);
                                            }

                                            await broadcaster.BroadcastDialogAsync(dialogData);
                                        }
                                    } catch (Exception ex) {
                                        Logger.Error($"Failed to broadcast dialog update via SignalR: {ex}");
                                    }
                                });
                            }, null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite),
                            // Update existing timer
                            (key, existingTimer) => {
                                existingTimer.Change(100, System.Threading.Timeout.Infinite); // Reset to 100ms
                                return existingTimer;
                            });

                        // Start/restart the timer
                        timer.Change(100, System.Threading.Timeout.Infinite);
                    };

                    // Subscribe the handler
                    notifyObj.PropertyChanged += handler;

                    // Store handler for cleanup
                    _eventHandlers[content] = handler;
                }

                // Subscribe to PlateSolveHistory collection changes for PlateSolvingStatusVM
                if (content?.GetType().FullName == "NINA.WPF.Base.ViewModel.PlateSolvingStatusVM") {
                    var historyProp = content.GetType().GetProperty("PlateSolveHistory");
                    if (historyProp != null) {
                        var historyCollection = historyProp.GetValue(content);
                        if (historyCollection is System.Collections.Specialized.INotifyCollectionChanged collectionChanged) {
                            collectionChanged.CollectionChanged += (sender, e) => {
                                _ = Task.Run(async () => {
                                    try {
                                        // Check if this content is still subscribed (dialog not closed)
                                        if (!_subscribedContent.ContainsKey(content)) {
                                            return; // Dialog was closed, don't broadcast
                                        }

                                        var broadcaster = NINA.Core.SignalR.DialogBroadcaster.Instance;
                                        if (broadcaster != null) {
                                            var parameters = dialog.Content.ToDictionary(
                                                kvp => kvp.Key,
                                                kvp => kvp.Value?.ToString() ?? ""
                                            );

                                            var dialogData = new Model.DialogData {
                                                Title = title ?? "Dialog",
                                                ContentType = dialog.ContentType,
                                                Active = true,
                                                Status = ExtractMessage(content),
                                                Parameters = parameters,
                                                AvailableCommands = new List<string> { "Cancel" },
                                                SlewAndCenter = ExtractSlewAndCenterData(content),
                                                MeridianFlip = ExtractMeridianFlipData(content)
                                            };

                                            await broadcaster.BroadcastDialogAsync(dialogData);
                                        }
                                    } catch (Exception ex) {
                                        Logger.Error($"Failed to broadcast dialog update after collection change: {ex}");
                                    }
                                });
                            };
                        }
                    }
                }

                // Add buttons: use ICommand properties from the content when available,
                // otherwise fall back to a generic Cancel button.
                var commandButtons = ExtractCommandButtons(content);
                var availableCommandNames = new List<string>();
                if (commandButtons.Count > 0) {
                    foreach (var (name, cmd, isDefault, isCancel) in commandButtons) {
                        var capturedCmd = cmd;
                        DialogService.AddButton(dialogId, name, name, isDefault: isDefault, isCancel: isCancel,
                            onClick: () => { try { if (capturedCmd.CanExecute(null)) capturedCmd.Execute(null); } catch { } });
                        availableCommandNames.Add(name);
                    }
                } else {
                    DialogService.AddButton(dialogId, "Cancel", "Cancel", isDefault: false, isCancel: true, onClick: null);
                    availableCommandNames.Add("Cancel");
                }

                // Touch-N-Stars' DialogModal.handleClose (the modal's X) auto-clicks the sole visible button when
                // there is exactly one, otherwise clicks a button named "PART_CloseButton" — a WPF naming
                // convention that has no real button behind it here, so without this it silently does nothing
                // whenever a dialog has zero or two-or-more visible buttons (any self-closing VM with more than
                // one command, e.g. Apply+Cancel). Register it as a working hidden button that closes the window
                // directly — matching real WPF's own default X-button behavior, which closes the window rather
                // than invoking any particular VM command — instead of changing the frontend's per-dialog-type
                // guesswork. Never added to availableCommandNames, so it broadcasts no visible extra button.
                DialogService.AddButton(dialogId, "PART_CloseButton", "PART_CloseButton", isDefault: false, isCancel: true,
                    onClick: () => { _ = Close(); });

                // Broadcast via SignalR immediately
                _ = Task.Run(async () => {
                    try {
                        // Check if this content is still subscribed (dialog not closed immediately).
                        // Only applies to INotifyPropertyChanged content — other content types were
                        // never added to _subscribedContent, so skipping the check for them is correct.
                        if (content is INotifyPropertyChanged && !_subscribedContent.ContainsKey(content)) {
                            return; // Dialog was closed before broadcast, don't broadcast
                        }

                        var broadcaster = SignalR.DialogBroadcaster.Instance;
                        if (broadcaster != null) {
                            // Convert object dictionary to string dictionary for Parameters
                            var parameters = dialog.Content.ToDictionary(
                                kvp => kvp.Key,
                                kvp => kvp.Value?.ToString() ?? ""
                            );

                            var dialogData = new Model.DialogData {
                                Title = title ?? "Dialog",
                                ContentType = dialog.ContentType,
                                Active = true,
                                Status = ExtractMessage(content),
                                Parameters = parameters,
                                AvailableCommands = availableCommandNames
                            };

                            // Special handling for PlateSolvingStatusVM
                            if (content?.GetType().FullName == "NINA.WPF.Base.ViewModel.PlateSolvingStatusVM") {
                                dialogData.SlewAndCenter = ExtractSlewAndCenterData(content);
                            }

                            // Special handling for MeridianFlipVM
                            if (content?.GetType().FullName == "NINA.WPF.Base.ViewModel.MeridianFlipVM") {
                                dialogData.MeridianFlip = ExtractMeridianFlipData(content);
                            }

                            await broadcaster.BroadcastDialogAsync(dialogData);
                        }
                    } catch (Exception ex) {
                        Logger.Error($"Failed to broadcast dialog via SignalR: {ex}");
                    }
                });

                return dialogId;
            } catch (Exception ex) {
                Logger.Error($"WindowService.ShowViaDialogService() failed: {ex}");
                return -1;
            }
        }

        /// <summary>
        /// Scans well-known ICommand properties on the content object and returns button descriptors.
        /// </summary>
        // Property names carrying known display text / styling, used by dialog VMs whose button row is
        // otherwise handled by a bespoke frontend component (MeridianFlip, ManualRotator, PlateSolving, ...).
        // Kept as an exact whitelist so those dialogs' button sets don't change.
        private static readonly (string PropName, string DisplayName, bool IsDefault, bool IsCancel)[] KnownCommands = new[] {
            ("ContinueCommand",  "Continue", true,  false),
            ("OKCommand",        "OK",        true,  false),
            ("YesCommand",       "Yes",       true,  false),
            ("ConfirmCommand",   "Confirm",   true,  false),
            ("CancelCommand",    "Cancel",    false, true),
            ("NoCommand",        "No",        false, false),
            ("AbortCommand",     "Abort",     false, true),
            // TelescopeLatLongSyncVM: the mount's and the profile's location differ on connect
            ("SyncTelescopeCommand", "Sync to mount",   false, false),
            ("SyncNINACommand",      "Sync from mount", false, false),
            ("SyncNoneCommand",      "No sync",         false, false),
        };

        private static List<(string Name, ICommand Command, bool IsDefault, bool IsCancel)> ExtractCommandButtons(object content) {
            var result = new List<(string, ICommand, bool, bool)>();
            if (content == null) return result;

            // A public "RequestClose" event signals a VM actually designed as a headless-safe modal choice (the
            // pattern ReplaySettingsPromptVM, ImportStarDetectionPreviewVM, and FrameReviewVMBase use; see
            // ReplaySettingsPrompt.cs in the HocusFocus plugin). Only those VMs get every public ICommand property
            // surfaced as a button — every one of them is an intentional, terminal dialog choice (must eventually
            // raise RequestClose, or the dialog can never close via a button). Known names still get their
            // preferred display text/styling instead of a humanized property name.
            //
            // VMs WITHOUT a RequestClose event never take this path — only the known-name allowlist below applies
            // to them — because a VM that merely happens to expose unrelated ICommand properties for other UI
            // purposes (e.g. TPAPAVM's DragMoveCommand / LeftMouseButtonDownCommand, used for mouse-drag gestures
            // on the polar-alignment chart) would otherwise have those turned into dialog buttons that do the
            // wrong thing when clicked.
            if (content.GetType().GetEvent("RequestClose") != null) {
                var knownByProp = KnownCommands.ToDictionary(k => k.PropName);
                foreach (var prop in content.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)) {
                    if (!typeof(ICommand).IsAssignableFrom(prop.PropertyType)) continue;
                    try {
                        if (prop.GetValue(content) is ICommand cmd) {
                            if (knownByProp.TryGetValue(prop.Name, out var known)) {
                                result.Add((known.DisplayName, cmd, known.IsDefault, known.IsCancel));
                            } else {
                                var displayName = HumanizeCommandPropertyName(prop.Name);
                                var lower = displayName.ToLowerInvariant();
                                bool isCancel = lower.Contains("cancel") || lower.Contains("close") || lower.Contains("abort");
                                bool isDefault = !isCancel && (lower.Contains("ok") || lower.Contains("yes") || lower.Contains("confirm") || lower.Contains("continue"));
                                result.Add((displayName, cmd, isDefault, isCancel));
                            }
                        }
                    } catch { }
                }
                return result;
            }

            foreach (var entry in KnownCommands) {
                var prop = content.GetType().GetProperty(entry.PropName);
                if (prop != null && typeof(ICommand).IsAssignableFrom(prop.PropertyType)) {
                    try {
                        if (prop.GetValue(content) is ICommand cmd) {
                            result.Add((entry.DisplayName, cmd, entry.IsDefault, entry.IsCancel));
                        }
                    } catch { }
                }
            }

            return result;
        }

        /// <summary>Turns "UseCaptureInMemoryCommand" into "Use Capture In Memory".</summary>
        private static string HumanizeCommandPropertyName(string propName) {
            var name = propName.EndsWith("Command", StringComparison.Ordinal)
                ? propName.Substring(0, propName.Length - "Command".Length)
                : propName;
            if (string.IsNullOrEmpty(name)) return propName;
            return Regex.Replace(name, "(?<!^)([A-Z])", " $1");
        }

        private string ExtractMessage(object obj) {
            if (obj == null) return "";

            var type = obj.GetType();
            var messageProp = type.GetProperty("Message") ?? type.GetProperty("Status");
            if (messageProp != null) {
                var value = messageProp.GetValue(obj);
                if (value != null) return value.ToString();
            }
            return "";
        }

        private Dictionary<string, object> ExtractProperties(object obj) {
            var result = new Dictionary<string, object>();
            if (obj == null) return result;

            var properties = obj.GetType().GetProperties();
            foreach (var prop in properties) {
                try {
                    var value = prop.GetValue(obj);
                    if (value != null) {
                        result[prop.Name] = value;
                    }
                } catch {
                    // Skip properties that can't be read
                }
            }
            return result;
        }

        private Model.SlewAndCenterData ExtractSlewAndCenterData(object content) {
            try {
                var type = content.GetType();

                // Get Status property (ApplicationStatus object)
                var statusProp = type.GetProperty("Status");
                var statusObj = statusProp?.GetValue(content);
                var statusMessage = "";

                if (statusObj != null) {
                    // ApplicationStatus has a Status property with the actual message
                    var statusType = statusObj.GetType();
                    var statusStringProp = statusType.GetProperty("Status") ?? statusType.GetProperty("Message");
                    if (statusStringProp != null) {
                        statusMessage = statusStringProp.GetValue(statusObj)?.ToString() ?? "";
                    } else {
                        statusMessage = statusObj.ToString();
                    }
                }

                // Get PlateSolveHistory collection
                var historyProp = type.GetProperty("PlateSolveHistory");
                var historyCollection = historyProp?.GetValue(content);

                var measurements = new List<Model.DialogMeasurement>();
                Model.DialogMeasurement currentMeasurement = null;

                if (historyCollection != null) {
                    var enumerableType = historyCollection as System.Collections.IEnumerable;
                    if (enumerableType != null) {
                        var itemsList = new List<object>();
                        foreach (var item in enumerableType) {
                            itemsList.Add(item);
                        }

                        // Get the last item as current measurement
                        if (itemsList.Count > 0) {
                            var lastItem = itemsList[itemsList.Count - 1];
                            currentMeasurement = ConvertToMeasurement(lastItem);
                        }

                        // Convert all items to measurements
                        foreach (var item in itemsList) {
                            var measurement = ConvertToMeasurement(item);
                            if (measurement != null) {
                                measurements.Add(measurement);
                            }
                        }
                    }
                }

                var result = new Model.SlewAndCenterData {
                    Active = true,
                    Status = statusMessage,
                    CurrentMeasurement = currentMeasurement,
                    Measurements = measurements
                };

                return result;
            } catch (Exception ex) {
                Logger.Error($"Failed to extract SlewAndCenter data: {ex}");
                return null;
            }
        }

        private Model.DialogMeasurement ConvertToMeasurement(object result) {
            try {
                var type = result.GetType();

                // Get SolveTime
                var solveTimeProp = type.GetProperty("SolveTime");
                var solveTime = solveTimeProp?.GetValue(result) as DateTime?;

                // Get Success
                var successProp = type.GetProperty("Success");
                var success = (bool)(successProp?.GetValue(result) ?? false);

                // Get Separation (error distance)
                var separationProp = type.GetProperty("Separation");
                var separation = separationProp?.GetValue(result);
                var errorDistance = separation?.ToString() ?? "--";

                // Get Position Angle (rotation)
                var posAngleProp = type.GetProperty("PositionAngle");
                var posAngle = posAngleProp?.GetValue(result);
                var rotation = posAngle is double d
                    ? (double.IsNaN(d) ? "--" : d.ToString("F2", CultureInfo.InvariantCulture))
                    : posAngle?.ToString() ?? "--";

                return new Model.DialogMeasurement {
                    Time = solveTime?.ToString("HH:mm:ss") ?? "",
                    Success = success,
                    ErrorDistance = errorDistance,
                    Rotation = rotation
                };
            } catch (Exception ex) {
                Logger.Error($"Failed to convert measurement: {ex}");
                return null;
            }
        }

        private Model.MeridianFlipData ExtractMeridianFlipData(object content) {
            try {
                var type = content.GetType();

                // Get Steps property (AutomatedWorkflow)
                var stepsProp = type.GetProperty("Steps");
                var stepsObj = stepsProp?.GetValue(content);

                // Get ActiveStep property to determine current step
                var activeStepProp = stepsObj?.GetType().GetProperty("ActiveStep");
                var activeStep = activeStepProp?.GetValue(stepsObj);

                var steps = new List<Model.MeridianFlipStep>();

                if (stepsObj != null && stepsObj is System.Collections.IEnumerable stepsCollection) {
                    foreach (var step in stepsCollection) {
                        if (step == null) continue;

                        var stepType = step.GetType();
                        var idProp = stepType.GetProperty("Id");
                        var titleProp = stepType.GetProperty("Title");
                        var finishedProp = stepType.GetProperty("Finished");
                        var isCurrentProp = stepType.GetProperty("IsCurrent");
                        var timeRemainingProp = stepType.GetProperty("TimeRemaining");

                        if (idProp != null && titleProp != null && finishedProp != null) {
                            var timeRemainingValue = timeRemainingProp?.GetValue(step);
                            double? timeRemaining = null;
                            if (timeRemainingValue != null && double.TryParse(timeRemainingValue.ToString(), out double timeValue)) {
                                timeRemaining = timeValue;
                            }

                            steps.Add(new Model.MeridianFlipStep {
                                Id = idProp.GetValue(step)?.ToString() ?? "",
                                Title = titleProp.GetValue(step)?.ToString() ?? "",
                                Finished = (bool)(finishedProp.GetValue(step) ?? false),
                                IsCurrent = activeStep == step,
                                TimeRemaining = timeRemaining
                            });
                        }
                    }
                }

                var result = new Model.MeridianFlipData {
                    Active = true,
                    StepCount = steps.Count,
                    Steps = steps
                };

                return result;
            } catch (Exception ex) {
                Logger.Error($"Failed to extract MeridianFlip data: {ex}");
                return null;
            }
        }
    }

    public interface IWindowService {

        void Show(object content, string title = "", ResizeMode resizeMode = ResizeMode.NoResize, WindowStyle windowStyle = WindowStyle.None);

        IDispatcherOperationWrapper ShowDialog(object content, string title = "", ResizeMode resizeMode = ResizeMode.NoResize, WindowStyle windowStyle = WindowStyle.None, ICommand closeCommand = null);

        event EventHandler OnDialogResultChanged;

        event EventHandler OnClosed;

        void DelayedClose(TimeSpan t);

        Task Close();
    }

    public interface IDispatcherOperationWrapper {
        Dispatcher Dispatcher { get; }
        DispatcherPriority Priority { get; set; }
        DispatcherOperationStatus Status { get; }
        Task Task { get; }
        object Result { get; }

        TaskAwaiter GetAwaiter();

        DispatcherOperationStatus Wait();

        DispatcherOperationStatus Wait(TimeSpan timeout);

        bool Abort();

        event EventHandler Aborted;

        event EventHandler Completed;
    }

    public class DispatcherOperationWrapper : IDispatcherOperationWrapper {
        private readonly DispatcherOperation op;

        public DispatcherOperationWrapper(DispatcherOperation operation) {
            op = operation;
        }

        public Dispatcher Dispatcher => op.Dispatcher;

        public DispatcherPriority Priority {
            get => op.Priority;
            set => op.Priority = value;
        }

        public DispatcherOperationStatus Status => op.Status;
        public Task Task => op.Task;

        [Browsable(false)]
        [EditorBrowsable(EditorBrowsableState.Never)]
        public TaskAwaiter GetAwaiter() {
            return op.GetAwaiter();
        }

        public DispatcherOperationStatus Wait() {
            return op.Wait();
        }

        [SecurityCritical]
        public DispatcherOperationStatus Wait(TimeSpan timeout) {
            return op.Wait(timeout);
        }

        public bool Abort() {
            return op.Abort();
        }

        public object Result => op.Result;

        public event EventHandler Aborted {
            add => op.Aborted += value;
            remove => op.Aborted -= value;
        }

        public event EventHandler Completed {
            add => op.Completed += value;
            remove => op.Completed -= value;
        }
    }

    /// <summary>
    /// A lightweight wrapper used for headless mode so ShowDialog() returns a Task
    /// that completes when the DialogService dialog is closed.
    /// </summary>
    public class HeadlessDispatcherOperationWrapper : IDispatcherOperationWrapper {
        private readonly Dispatcher _dispatcher;
        private readonly Task _task;
        private DispatcherPriority _priority = DispatcherPriority.Normal;

        public HeadlessDispatcherOperationWrapper(Dispatcher dispatcher, Task task) {
            _dispatcher = dispatcher;
            _task = task ?? Task.CompletedTask;
            _task.ContinueWith(t => Completed?.Invoke(this, EventArgs.Empty), TaskScheduler.Default);
        }

        public Dispatcher Dispatcher => _dispatcher;
        public DispatcherPriority Priority { get => _priority; set => _priority = value; }
        public DispatcherOperationStatus Status => _task.IsCompleted ? DispatcherOperationStatus.Completed : DispatcherOperationStatus.Pending;
        public Task Task => _task;
        public object Result => null;
        public TaskAwaiter GetAwaiter() => _task.GetAwaiter();

        public DispatcherOperationStatus Wait() {
            _task.Wait();
            return DispatcherOperationStatus.Completed;
        }

        public DispatcherOperationStatus Wait(TimeSpan timeout) {
            var completed = Task.WaitAny(new Task[] { _task }, timeout) == 0;
            return completed ? DispatcherOperationStatus.Completed : DispatcherOperationStatus.Pending;
        }

        public bool Abort() { return false; }

        public event EventHandler Aborted;
        public event EventHandler Completed;
    }

    public class DialogResultEventArgs : EventArgs {

        public DialogResultEventArgs(bool? dialogResult) {
            DialogResult = dialogResult;
        }

        public bool? DialogResult { get; set; }
    }
}
