#region "copyright"

/*
    Copyright © 2025 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

namespace System.Windows {
    using System;
    using System.Collections.Generic;
    using System.Reflection;
    using System.Runtime.CompilerServices;
    /// <summary>
    /// Provides weak event subscriptions so that listeners can be garbage-collected
    /// without first unsubscribing from the event source.
    /// </summary>
    /// <typeparam name="TEventSource">The type of the event source.</typeparam>
    /// <typeparam name="TEventArgs">The type of the event args.</typeparam>
    public static class WeakEventManager<TEventSource, TEventArgs> where TEventSource : class where TEventArgs : EventArgs {
        private static readonly object _lock = new object();

        // Keyed by source reference identity (ConditionalWeakTable), not a hash code - two
        // distinct live sources can never be merged into the same bucket the way they could
        // with a GetHashCode-based Dictionary key. Each source maps to its subscriptions
        // grouped by event name, since one source can have several independently-tracked events.
        private static readonly ConditionalWeakTable<TEventSource,
            Dictionary<string, List<(WeakReference targetRef, MethodInfo method, Delegate wrapper)>>> _subscriptions = new();

        // Real production events are rarely declared as `EventHandler<TEventArgs>` itself -
        // e.g. InputTarget.CoordinatesChanged / IProfileService.LocationChanged are plain
        // `event EventHandler`, and INotifyPropertyChanged.PropertyChanged is
        // `PropertyChangedEventHandler`. A C# lambda is permanently typed as the exact delegate
        // type it's written against (EventHandler<TEventArgs>), and EventInfo.AddEventHandler
        // throws ArgumentException when that doesn't match the event's real delegate type - which
        // the old code's blanket catch swallowed, so the subscription silently never attached.
        // This trampoline exposes a plain (object, TEventArgs) instance method so
        // Delegate.CreateDelegate can bind a delegate of whatever type the real event declares.
        //
        // The binding must go through the MethodInfo-based CreateDelegate overload: only that
        // family supports relaxed (contravariant-parameter) binding, which is needed when the
        // event's args type derives from TEventArgs (e.g. subscribing MouseLeftButtonDown, a
        // MouseButtonEventArgs event, through WeakEventManager<T, MouseEventArgs>). The
        // name-based overload requires an exact signature match and would throw instead.
        private static readonly MethodInfo HandleMethod = typeof(Trampoline).GetMethod(nameof(Trampoline.Handle));

        private sealed class Trampoline {
            private readonly WeakReference targetRef;
            private readonly MethodInfo method;
            private readonly TEventSource source;
            private readonly string eventName;
            private readonly EventInfo eventInfo;
            public Delegate Wrapper;

            public Trampoline(WeakReference targetRef, MethodInfo method, TEventSource source, string eventName, EventInfo eventInfo) {
                this.targetRef = targetRef;
                this.method = method;
                this.source = source;
                this.eventName = eventName;
                this.eventInfo = eventInfo;
            }

            public void Handle(object sender, TEventArgs args) {
                var target = targetRef.Target;
                if (target != null) {
                    method.Invoke(target, new object[] { sender, args });
                    return;
                }

                // The subscriber was collected - self-prune right away instead of waiting for
                // someone to call RemoveHandler, so dead entries (and the reflection invoke on
                // every future fire) don't accumulate for the rest of the process.
                lock (_lock) {
                    if (_subscriptions.TryGetValue(source, out var perEvent) &&
                        perEvent.TryGetValue(eventName, out var list)) {
                        list.RemoveAll(e => ReferenceEquals(e.wrapper, Wrapper));
                        if (list.Count == 0) {
                            perEvent.Remove(eventName);
                        }
                    }
                }
                eventInfo.RemoveEventHandler(source, Wrapper);
            }
        }

        /// <summary>
        /// Adds a weak event handler for the specified event.
        /// The handler's target is held via a WeakReference so it can be collected.
        /// </summary>
        public static void AddHandler(TEventSource source, string eventName, EventHandler<TEventArgs> handler) {
            if (source == null || string.IsNullOrEmpty(eventName) || handler == null) {
                return;
            }

            EventInfo eventInfo;
            try {
                eventInfo = typeof(TEventSource).GetEvent(eventName);
            } catch {
                // Reflection can throw (e.g. AmbiguousMatchException) for pathological type shapes.
                return;
            }
            if (eventInfo == null) {
                return;
            }

            Delegate wrapper;
            WeakReference weakTarget = null;
            if (handler.Target == null) {
                // Static method - nothing to prevent from leaking, but still bind through
                // CreateDelegate so a delegate-type mismatch gets relaxed binding, and record
                // the subscription (targetRef == null marks it static) so RemoveHandler can
                // detach it again.
                wrapper = Delegate.CreateDelegate(eventInfo.EventHandlerType, handler.Method, throwOnBindFailure: false);
            } else {
                weakTarget = new WeakReference(handler.Target);
                var trampoline = new Trampoline(weakTarget, handler.Method, source, eventName, eventInfo);
                wrapper = Delegate.CreateDelegate(eventInfo.EventHandlerType, trampoline, HandleMethod, throwOnBindFailure: false);
                trampoline.Wrapper = wrapper;
            }
            if (wrapper == null) {
                // Handle(object, TEventArgs) cannot be bound to the event's delegate type even
                // with relaxed rules (e.g. the event's args type is unrelated to TEventArgs).
                // Preserve the old silent-no-op contract rather than throwing at the call site.
                return;
            }

            lock (_lock) {
                var perEvent = _subscriptions.GetValue(source, _ => new Dictionary<string,
                    List<(WeakReference, MethodInfo, Delegate)>>());
                if (!perEvent.TryGetValue(eventName, out var list)) {
                    list = new List<(WeakReference, MethodInfo, Delegate)>();
                    perEvent[eventName] = list;
                }
                list.Add((weakTarget, handler.Method, wrapper));
            }

            eventInfo.AddEventHandler(source, wrapper);
        }

        /// <summary>
        /// Removes a weak event handler.
        /// </summary>
        public static void RemoveHandler(TEventSource source, string eventName, EventHandler<TEventArgs> handler) {
            if (source == null || string.IsNullOrEmpty(eventName) || handler == null) {
                return;
            }

            EventInfo eventInfo;
            try {
                eventInfo = typeof(TEventSource).GetEvent(eventName);
            } catch {
                return;
            }
            if (eventInfo == null) return;

            lock (_lock) {
                if (_subscriptions.TryGetValue(source, out var perEvent) &&
                    perEvent.TryGetValue(eventName, out var list)) {
                    for (int i = list.Count - 1; i >= 0; i--) {
                        var (targetRef, method, wrapper) = list[i];
                        if (targetRef == null) {
                            // Static subscription - never collected; remove only on an exact match.
                            if (handler.Target == null && method == handler.Method) {
                                eventInfo.RemoveEventHandler(source, wrapper);
                                list.RemoveAt(i);
                            }
                            continue;
                        }
                        var target = targetRef.Target;
                        // Remove if target was collected, or if it matches the handler being removed
                        if (target == null || (target == handler.Target && method == handler.Method)) {
                            eventInfo.RemoveEventHandler(source, wrapper);
                            list.RemoveAt(i);
                        }
                    }
                    if (list.Count == 0) {
                        perEvent.Remove(eventName);
                    }
                }
            }
        }
    }
}
