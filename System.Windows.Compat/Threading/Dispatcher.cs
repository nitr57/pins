#region "copyright"

/*
    Copyright © 2025 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

namespace System.Windows.Threading {
    /// <summary>
    /// Stub implementation of WPF Dispatcher for headless execution
    /// </summary>
    public class Dispatcher {
        // Overload for WPF compatibility
        public void Invoke(DispatcherPriority priority, Action callback) {
            callback();
        }

        // Overload for WPF compatibility
        public DispatcherOperation BeginInvoke(DispatcherPriority priority, Action callback) {
            var task = System.Threading.Tasks.Task.Run(callback);
            var op = new DispatcherOperation {
                Dispatcher = this,
                Priority = priority,
                Task = task
            };
            return op;
        }
        private static Dispatcher _currentDispatcher = new Dispatcher();

        public static Dispatcher CurrentDispatcher => _currentDispatcher;

        /// <summary>
        /// Headless there are no per-thread dispatchers, only the shared one, so no thread owns a
        /// dispatcher. Returning null (WPF's answer for such a thread) also keeps callers that shut
        /// down "their" thread's dispatcher from shutting down the shared one.
        /// </summary>
        public static Dispatcher FromThread(System.Threading.Thread thread) => null;

        public T Invoke<T>(Func<T> callback) {
            // In headless mode, just execute the callback directly
            return callback();
        }

        public void Invoke(Action callback) {
            callback();
        }

        public object Invoke(Delegate method, params object[] args) {
            // Execute the delegate with the provided arguments
            return method?.DynamicInvoke(args);
        }

        public DispatcherOperation InvokeAsync(Action callback) {
            callback();
            return new DispatcherOperation { Dispatcher = this };
        }

        public DispatcherOperation<T> InvokeAsync<T>(Func<T> callback) {
            return new DispatcherOperation<T>(System.Threading.Tasks.Task.FromResult(callback()));
        }

        public System.Threading.Tasks.Task InvokeAsync(Action callback, DispatcherPriority priority, System.Threading.CancellationToken cancellationToken = default) {
            // Ignore priority in headless mode
            if (cancellationToken.IsCancellationRequested) {
                return System.Threading.Tasks.Task.FromCanceled(cancellationToken);
            }
            callback();
            return System.Threading.Tasks.Task.CompletedTask;
        }

        public DispatcherOperation<T> InvokeAsync<T>(Func<T> callback, DispatcherPriority priority, System.Threading.CancellationToken cancellationToken = default) {
            // Ignore priority in headless mode
            if (cancellationToken.IsCancellationRequested) {
                return new DispatcherOperation<T>(System.Threading.Tasks.Task.FromCanceled<T>(cancellationToken));
            }
            return new DispatcherOperation<T>(System.Threading.Tasks.Task.FromResult(callback()));
        }

        public System.Threading.Tasks.Task BeginInvoke(Action callback) {
            return System.Threading.Tasks.Task.Run(callback);
        }

        public System.Threading.Tasks.Task BeginInvoke(Action callback, DispatcherPriority priority) {
            // Ignore priority in headless mode
            return System.Threading.Tasks.Task.Run(callback);
        }

        public System.Threading.Tasks.Task BeginInvoke(DispatcherPriority priority, Delegate method, params object[] args) {
            // Ignore priority in headless mode
            return System.Threading.Tasks.Task.Run(() => method?.DynamicInvoke(args));
        }

        public bool CheckAccess() {
            // In headless mode, always return true (we can access from any thread)
            return true;
        }

        /// <summary>
        /// Throws if the caller is on the wrong thread. Since CheckAccess always succeeds here,
        /// this never throws - it exists so affinity-asserting callers compile and run unchanged.
        /// </summary>
        public void VerifyAccess() { }

        private readonly System.Threading.ManualResetEventSlim _shutdownRequested = new System.Threading.ManualResetEventSlim(false);
        private volatile bool _shutdownStarted;
        private volatile bool _shutdownFinished;

        public bool HasShutdownStarted => _shutdownStarted;
        public bool HasShutdownFinished => _shutdownFinished;

        /// <summary>
        /// Blocks the calling thread until <see cref="InvokeShutdown"/> is called on its
        /// dispatcher. There is no message queue to pump headless, so this delivers only the
        /// "keep the thread alive until told to stop" half of WPF's contract - work handed to
        /// the dispatcher runs on the caller's thread or the thread pool, not here.
        /// </summary>
        public static void Run() {
            Dispatcher dispatcher = CurrentDispatcher;
            dispatcher._shutdownRequested.Wait();
            dispatcher._shutdownFinished = true;
        }

        public void InvokeShutdown() {
            _shutdownStarted = true;
            _shutdownRequested.Set();
        }

        public void BeginInvokeShutdown(DispatcherPriority priority) {
            InvokeShutdown();
        }

        public static void PushFrame(DispatcherFrame frame) {
            // In headless mode, simulate frame processing by pumping the message loop
            // until Continue is set to false by another thread
            while (frame.Continue) {
                System.Threading.Thread.Sleep(10);
            }
        }
    }

    /// <summary>
    /// Priority levels for dispatcher operations (stub - priorities ignored in headless mode)
    /// </summary>
    public enum DispatcherPriority {
        Invalid = -1,
        Inactive = 0,
        SystemIdle = 1,
        ApplicationIdle = 2,
        ContextIdle = 3,
        Background = 4,
        Input = 5,
        Loaded = 6,
        Render = 7,
        DataBind = 8,
        Normal = 9,
        Send = 10
    }

    /// <summary>
    /// Timer that executes on the dispatcher (stub - uses System.Timers.Timer in headless mode)
    /// </summary>
    public class DispatcherTimer : IDisposable {
        private System.Timers.Timer _timer;

        private volatile bool _disposed;

        public DispatcherTimer() {
            _timer = new System.Timers.Timer();
            _timer.Elapsed += OnTimerElapsed;
        }

        public DispatcherTimer(DispatcherPriority priority) {
            // Ignore priority in headless mode
            _timer = new System.Timers.Timer();
            _timer.Elapsed += OnTimerElapsed;
        }

        public DispatcherTimer(DispatcherPriority priority, Dispatcher dispatcher) {
            // Ignore priority and dispatcher in headless mode
            _timer = new System.Timers.Timer();
            _timer.Elapsed += OnTimerElapsed;
        }

        public DispatcherTimer(System.TimeSpan interval, DispatcherPriority priority, System.EventHandler callback, Dispatcher dispatcher) {
            // Ignore priority and dispatcher in headless mode
            _timer = new System.Timers.Timer(interval.TotalMilliseconds);
            _timer.Elapsed += OnTimerElapsed;
            Tick += callback;
            // WPF's 4-argument constructor starts the timer as part of construction; callers
            // (e.g. ProfileSelectVM.Wait100msNonBlocking) rely on that and never call Start().
            Start();
        }

        private void OnTimerElapsed(object sender, System.Timers.ElapsedEventArgs e) {
            if (_disposed) return;
            Tick?.Invoke(this, EventArgs.Empty);
        }

        public TimeSpan Interval {
            get => TimeSpan.FromMilliseconds(_timer.Interval);
            set => _timer.Interval = value.TotalMilliseconds;
        }

        public bool IsEnabled {
            get => _timer.Enabled;
            set => _timer.Enabled = value;
        }

        public event EventHandler Tick;

        public void Start() => _timer.Start();
        public void Stop() => _timer.Stop();

        public void Dispose() {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
    }
}
