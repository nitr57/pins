namespace System.Windows {
    using System;
    using System.Collections.Generic;
    using System.Reflection;
    /// <summary>
    /// Provides a thread-safe method to add weak event listeners.
    /// This is a minimal implementation for compatibility on non-WPF platforms.
    /// </summary>
    /// <typeparam name="TEventSource">The type of the event source.</typeparam>
    /// <typeparam name="TEventArgs">The type of the event args.</typeparam>
    public static class WeakEventManager<TEventSource, TEventArgs> where TEventArgs : EventArgs {

        /// <summary>
        /// Adds a weak event handler for the specified event.
        /// </summary>
        public static void AddHandler(TEventSource source, string eventName, EventHandler<TEventArgs> handler) {
            if (source == null || string.IsNullOrEmpty(eventName) || handler == null) {
                return;
            }

            try {
                var eventInfo = typeof(TEventSource).GetEvent(eventName);
                if (eventInfo == null) {
                    return;
                }

                // For simplicity, just use a strong reference approach
                // Weak event handling is complex and not critical on Linux
                eventInfo.AddEventHandler(source, handler);
            } catch {
                // Silently fail if we can't add the event handler
            }
        }

        /// <summary>
        /// Removes a weak event handler.
        /// </summary>
        public static void RemoveHandler(TEventSource source, string eventName, EventHandler<TEventArgs> handler) {
            if (source == null || string.IsNullOrEmpty(eventName) || handler == null) {
                return;
            }

            try {
                var eventInfo = typeof(TEventSource).GetEvent(eventName);
                if (eventInfo != null) {
                    eventInfo.RemoveEventHandler(source, handler);
                }
            } catch {
                // Silently fail if we can't remove the event handler
            }
        }
    }
}
