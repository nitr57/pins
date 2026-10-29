#region "copyright"

/*
    Copyright © 2025 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NUnit.Framework;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;

namespace NINA.Test.SystemWindowsCompat {

    [TestFixture]
    public class ThreadingCompatTest {

        #region Dispatcher

        [Test]
        public void Dispatcher_Invoke_RunsInlineOnCallingThread() {
            int callingThread = Environment.CurrentManagedThreadId;
            int? executionThread = null;

            Dispatcher.CurrentDispatcher.Invoke(() => executionThread = Environment.CurrentManagedThreadId);

            Assert.That(executionThread, Is.EqualTo(callingThread));
        }

        [Test]
        public void Dispatcher_InvokeWithResult_ReturnsValue() {
            var result = Dispatcher.CurrentDispatcher.Invoke(() => 42);

            Assert.That(result, Is.EqualTo(42));
        }

        [Test]
        public void Dispatcher_CheckAccess_AlwaysTrue() {
            Assert.That(Dispatcher.CurrentDispatcher.CheckAccess(), Is.True);

            bool fromOtherThread = false;
            var thread = new Thread(() => fromOtherThread = Dispatcher.CurrentDispatcher.CheckAccess());
            thread.Start();
            thread.Join();

            Assert.That(fromOtherThread, Is.True);
        }

        [Test]
        public async Task Dispatcher_BeginInvoke_ExecutesAction() {
            bool executed = false;

            await Dispatcher.CurrentDispatcher.BeginInvoke(() => executed = true);

            Assert.That(executed, Is.True);
        }

        [Test]
        public async Task Dispatcher_InvokeAsync_ReturnsResult() {
            var result = await Dispatcher.CurrentDispatcher.InvokeAsync(() => "done");

            Assert.That(result, Is.EqualTo("done"));
        }

        [Test]
        public void Dispatcher_InvokeAsync_CancelledToken_ReturnsCanceledTask() {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var task = Dispatcher.CurrentDispatcher.InvokeAsync(
                () => { }, DispatcherPriority.Normal, cts.Token);

            Assert.That(task.IsCanceled, Is.True);
        }

        [Test]
        public void DispatcherSynchronizationContext_Send_ExecutesInline() {
            var context = new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher);
            bool executed = false;

            context.Send(_ => executed = true, null);

            Assert.That(executed, Is.True);
        }

        [Test]
        public void DispatcherTimer_Ticks() {
            using var timer = new DispatcherTimer();
            // Deliberately not disposed: a late tick racing Stop() must not hit a disposed event
            var fired = new ManualResetEventSlim(false);
            timer.Interval = TimeSpan.FromMilliseconds(50);
            timer.Tick += (s, e) => fired.Set();

            timer.Start();
            try {
                Assert.That(fired.Wait(TimeSpan.FromSeconds(5)), Is.True, "timer did not tick within 5s");
            } finally {
                timer.Stop();
            }
        }

        // WPF's 4-argument constructor starts the timer as part of construction; callers like
        // ProfileSelectVM.Wait100msNonBlocking rely on that and never call Start() themselves.
        [Test]
        public void DispatcherTimer_CallbackConstructor_AutoStarts() {
            var fired = new ManualResetEventSlim(false);
            using var timer = new DispatcherTimer(
                TimeSpan.FromMilliseconds(50),
                DispatcherPriority.Background,
                (s, e) => fired.Set(),
                Dispatcher.CurrentDispatcher);

            try {
                Assert.That(fired.Wait(TimeSpan.FromSeconds(5)), Is.True, "timer constructed with a callback must tick without an explicit Start()");
            } finally {
                timer.Stop();
            }
        }

        [Test]
        public void DispatcherTimer_Interval_RoundTrips() {
            using var timer = new DispatcherTimer();
            timer.Interval = TimeSpan.FromMilliseconds(123);

            Assert.That(timer.Interval, Is.EqualTo(TimeSpan.FromMilliseconds(123)));
        }

        // WPF runs every tick on its one dispatcher thread, so a handler slower than the interval delays the next tick
        [Test]
        public void DispatcherTimer_TicksNeverOverlap() {
            using var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            int running = 0;
            int maxRunning = 0;
            int ticks = 0;
            timer.Tick += (s, e) => {
                int now = Interlocked.Increment(ref running);
                InterlockedMax(ref maxRunning, now);
                Thread.Sleep(50);
                Interlocked.Decrement(ref running);
                Interlocked.Increment(ref ticks);
            };

            timer.Start();
            Thread.Sleep(600);
            timer.Stop();

            Assert.That(Volatile.Read(ref ticks), Is.GreaterThan(2), "it keeps ticking");
            Assert.That(Volatile.Read(ref maxRunning), Is.EqualTo(1));
        }

        [Test]
        public void DispatcherTimer_StopInATick_EndsTheTicks_AndIsEnabledStaysTrueWhileRunning() {
            using var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
            int ticks = 0;
            bool enabledInTick = false;
            timer.Tick += (s, e) => {
                enabledInTick = timer.IsEnabled;
                Interlocked.Increment(ref ticks);
                timer.Stop();
            };

            timer.Start();
            Thread.Sleep(300);

            Assert.That(Volatile.Read(ref ticks), Is.EqualTo(1));
            Assert.That(enabledInTick, Is.True);
            Assert.That(timer.IsEnabled, Is.False);
        }

        [Test]
        public void DispatcherTimer_AfterDispose_DoesNothing() {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
            int ticks = 0;
            timer.Tick += (s, e) => Interlocked.Increment(ref ticks);
            timer.Dispose();

            Assert.DoesNotThrow(() => {
                timer.Start();
                timer.IsEnabled = true;
                timer.Interval = TimeSpan.FromMilliseconds(30);
                _ = timer.Interval;
                timer.Stop();
                timer.Dispose();
            });
            Thread.Sleep(100);
            Assert.That(Volatile.Read(ref ticks), Is.Zero);
            Assert.That(timer.IsEnabled, Is.False);
        }

        private static void InterlockedMax(ref int target, int value) {
            int current;
            while (value > (current = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, current) != current) {
            }
        }

        // REVIEW.md F18: DispatcherOperation.Status was fixed to Completed at creation time even
        // though the backing Task.Run may still be executing, and Wait() returned immediately
        // without actually waiting.
        [Test]
        public void DispatcherOperation_Wait_BlocksUntilCallbackFinishes() {
            var done = false;
            var op = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Normal, () => {
                Thread.Sleep(100);
                done = true;
            });

            var status = op.Wait();

            Assert.That(done, Is.True, "Wait() must block until the callback actually finishes");
            Assert.That(status, Is.EqualTo(DispatcherOperationStatus.Completed));
            Assert.That(op.Status, Is.EqualTo(DispatcherOperationStatus.Completed));
        }

        #endregion

        #region WeakEventManager

        private class EventSource {
            public event EventHandler<EventArgs>? Fired;
            public event EventHandler<EventArgs>? Fired2;

            public void Raise() => Fired?.Invoke(this, EventArgs.Empty);

            public void Raise2() => Fired2?.Invoke(this, EventArgs.Empty);
        }

        private class Counter {
            public int Count;
        }

        private class Listener {
            private readonly Counter counter;

            public Listener(Counter counter) {
                this.counter = counter;
            }

            public void Handle(object? sender, EventArgs e) => counter.Count++;
        }

        [Test]
        public void WeakEventManager_AddHandler_ReceivesEvents() {
            var source = new EventSource();
            var counter = new Counter();
            var listener = new Listener(counter);

            WeakEventManager<EventSource, EventArgs>.AddHandler(source, nameof(EventSource.Fired), listener.Handle);
            source.Raise();
            source.Raise();

            Assert.That(counter.Count, Is.EqualTo(2));
            GC.KeepAlive(listener);
        }

        [Test]
        public void WeakEventManager_RemoveHandler_StopsEvents() {
            var source = new EventSource();
            var counter = new Counter();
            var listener = new Listener(counter);

            WeakEventManager<EventSource, EventArgs>.AddHandler(source, nameof(EventSource.Fired), listener.Handle);
            source.Raise();
            WeakEventManager<EventSource, EventArgs>.RemoveHandler(source, nameof(EventSource.Fired), listener.Handle);
            source.Raise();

            Assert.That(counter.Count, Is.EqualTo(1));
            GC.KeepAlive(listener);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference SubscribeTransientListener(EventSource source, Counter counter) {
            var listener = new Listener(counter);
            WeakEventManager<EventSource, EventArgs>.AddHandler(source, nameof(EventSource.Fired), listener.Handle);
            return new WeakReference(listener);
        }

        [Test]
        public void WeakEventManager_CollectedTarget_IsNoLongerInvoked() {
            var source = new EventSource();
            var counter = new Counter();

            var weakListener = SubscribeTransientListener(source, counter);
            source.Raise();
            Assert.That(counter.Count, Is.EqualTo(1));

            for (int i = 0; i < 5 && weakListener.IsAlive; i++) {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            if (weakListener.IsAlive) {
                Assert.Inconclusive("listener was not collected; cannot verify weak behavior in this run");
            }

            source.Raise();
            Assert.That(counter.Count, Is.EqualTo(1), "a collected listener must not be invoked");
        }

        [Test]
        public void WeakEventManager_UnknownEventName_IsIgnored() {
            var source = new EventSource();
            var counter = new Counter();
            var listener = new Listener(counter);

            Assert.DoesNotThrow(() =>
                WeakEventManager<EventSource, EventArgs>.AddHandler(source, "NoSuchEvent", listener.Handle));
        }

        // Real production sources (InputTarget.CoordinatesChanged, IProfileService.LocationChanged, ...)
        // declare their event as plain non-generic `event EventHandler`, not `event EventHandler<EventArgs>`
        // like the EventSource helper above. AddHandler must still attach correctly in that shape.
        private class NonGenericEventSource {
            public event EventHandler? Fired;

            public void Raise() => Fired?.Invoke(this, EventArgs.Empty);
        }

        [Test]
        public void WeakEventManager_NonGenericEventHandlerSource_ReceivesEvents() {
            var source = new NonGenericEventSource();
            var counter = new Counter();
            var listener = new Listener(counter);

            WeakEventManager<NonGenericEventSource, EventArgs>.AddHandler(source, nameof(NonGenericEventSource.Fired), listener.Handle);
            source.Raise();
            source.Raise();

            Assert.That(counter.Count, Is.EqualTo(2));
            GC.KeepAlive(listener);
        }

        [Test]
        public void WeakEventManager_MultipleEventNamesOnSameSource_TrackedIndependently() {
            // Same source instance, two different event names, under the same closed generic
            // type (WeakEventManager<EventSource, EventArgs>) - removing one must not disturb
            // the other, and both must keep firing independently.
            var source = new EventSource();
            var firedCounter = new Counter();
            var fired2Counter = new Counter();
            var firedListener = new Listener(firedCounter);
            var fired2Listener = new Listener(fired2Counter);

            WeakEventManager<EventSource, EventArgs>.AddHandler(source, nameof(EventSource.Fired), firedListener.Handle);
            WeakEventManager<EventSource, EventArgs>.AddHandler(source, nameof(EventSource.Fired2), fired2Listener.Handle);

            source.Raise();
            source.Raise2();
            Assert.That(firedCounter.Count, Is.EqualTo(1));
            Assert.That(fired2Counter.Count, Is.EqualTo(1));

            WeakEventManager<EventSource, EventArgs>.RemoveHandler(source, nameof(EventSource.Fired), firedListener.Handle);
            source.Raise();
            source.Raise2();

            Assert.That(firedCounter.Count, Is.EqualTo(1), "Fired was removed and must not increase");
            Assert.That(fired2Counter.Count, Is.EqualTo(2), "Fired2 must be unaffected by removing Fired");
            GC.KeepAlive(firedListener);
            GC.KeepAlive(fired2Listener);
        }

        #endregion

        #region CommandManager

        [Test]
        public void CommandManager_InvalidateRequerySuggested_RaisesEvent() {
            int raised = 0;
            EventHandler handler = (s, e) => raised++;

            System.Windows.Input.CommandManager.RequerySuggested += handler;
            try {
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
                Assert.That(raised, Is.EqualTo(1));
            } finally {
                System.Windows.Input.CommandManager.RequerySuggested -= handler;
            }

            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            Assert.That(raised, Is.EqualTo(1), "handler must not fire after unsubscribe");
        }

        #endregion
    }
}
