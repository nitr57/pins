#region "copyright"

/*
    Copyright � 2016 - 2024 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Core.Utility.Extensions;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security;
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
            var window = new CustomWindow() {
                SizeToContent = SizeToContent.WidthAndHeight,
                Title = title,
                Background = Application.Current.TryFindResource("BackgroundBrush") as Brush,
                ResizeMode = resizeMode,
                WindowStyle = windowStyle,
                MinHeight = 300,
                MinWidth = 350,
                Style = Application.Current.TryFindResource("NoResizeWindow") as Style,
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

        private void ShowViaDialogService(object content, string title) {
            try {
                var dialog = new DialogService.DialogInfo {
                    Title = title ?? "Dialog",
                    Message = ExtractMessage(content),
                    ContentType = content?.GetType().FullName ?? "UnknownWindow",
                    DataContext = content
                };

                // Extract content properties
                if (content != null) {
                    dialog.Content = ExtractProperties(content);
                }

                int dialogId = DialogService.RegisterDialog(dialog);
                
                // Subscribe to property changes for real-time updates
                if (content is INotifyPropertyChanged notifyObj) {
                    notifyObj.PropertyChanged += (sender, e) => {
                        _ = Task.Run(async () => {
                            try {
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
                                        AvailableCommands = ["Cancel"]
                                    };

                                    // Special handling for PlateSolvingStatusVM
                                    if (content?.GetType().FullName == "NINA.WPF.Base.ViewModel.PlateSolvingStatusVM") {
                                        dialogData.SlewAndCenter = ExtractSlewAndCenterData(content);
                                    }

                                    await broadcaster.BroadcastDialogAsync(dialogData);
                                }
                            } catch (Exception ex) {
                                Logger.Error($"Failed to broadcast dialog update via SignalR: {ex}");
                            }
                        });
                    };
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
                                                AvailableCommands = ["Cancel"],
                                                SlewAndCenter = ExtractSlewAndCenterData(content)
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
                
                // Add a Cancel button (all dialogs should be cancellable)
                DialogService.AddButton(dialogId, "Cancel", "Cancel", isDefault: false, isCancel: true, onClick: null);
                
                // Broadcast via SignalR immediately
                _ = Task.Run(async () => {
                    try {
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
                                AvailableCommands = ["Cancel"]
                            };

                            // Special handling for PlateSolvingStatusVM
                            if (content?.GetType().FullName == "NINA.WPF.Base.ViewModel.PlateSolvingStatusVM") {
                                dialogData.SlewAndCenter = ExtractSlewAndCenterData(content);
                            }
                            
                            await broadcaster.BroadcastDialogAsync(dialogData);
                        }
                    } catch (Exception ex) {
                        Logger.Error($"Failed to broadcast dialog via SignalR: {ex}");
                    }
                });
            } catch (Exception ex) {
                Logger.Error($"WindowService.ShowViaDialogService() failed: {ex}");
            }
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
                var rotation = posAngle?.ToString() ?? "--";

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

    public class DialogResultEventArgs : EventArgs {

        public DialogResultEventArgs(bool? dialogResult) {
            DialogResult = dialogResult;
        }

        public bool? DialogResult { get; set; }
    }
}