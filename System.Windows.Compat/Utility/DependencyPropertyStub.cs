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
    // Minimal stub for DependencyProperty
    public class DependencyProperty {
        private PropertyMetadata metadata;

        /// <summary>
        /// Represents an unset value for a dependency property.
        /// </summary>
        public static readonly object UnsetValue = new object();

        public static DependencyProperty Register(string name, System.Type propertyType, System.Type ownerType) 
            => new DependencyProperty { metadata = new PropertyMetadata() };

        public static DependencyProperty Register(string name, System.Type propertyType, System.Type ownerType, PropertyMetadata metadata) 
            => new DependencyProperty { metadata = metadata };

        public static DependencyProperty Register(string name, System.Type propertyType, System.Type ownerType, object metadata) 
            => new DependencyProperty { metadata = metadata as PropertyMetadata ?? new PropertyMetadata() };

        public static DependencyProperty RegisterAttached(string name, System.Type propertyType, System.Type ownerType, PropertyMetadata metadata) 
            => new DependencyProperty { metadata = metadata };

        public static DependencyProperty RegisterAttached(string name, System.Type propertyType, System.Type ownerType, object metadata) 
            => new DependencyProperty { metadata = metadata as PropertyMetadata ?? new PropertyMetadata() };

        public PropertyMetadata GetMetadata(System.Type forType) {
            return metadata ?? new PropertyMetadata();
        }
    }

    /// <summary>
    /// Provides metadata for dependency properties.
    /// </summary>
    public class PropertyMetadata {
        public PropertyMetadata() { }

        public PropertyMetadata(object defaultValue) {
            DefaultValue = defaultValue;
        }

        public PropertyMetadata(object defaultValue, PropertyChangedCallback propertyChangedCallback) {
            DefaultValue = defaultValue;
            PropertyChangedCallback = propertyChangedCallback;
        }

        public object DefaultValue { get; set; }
        public PropertyChangedCallback PropertyChangedCallback { get; set; }
    }

    /// <summary>
    /// Provides metadata for framework-level dependency properties.
    /// </summary>
    public class FrameworkPropertyMetadata : PropertyMetadata {
        public FrameworkPropertyMetadata() { }

        public FrameworkPropertyMetadata(object defaultValue) : base(defaultValue) { }

        public FrameworkPropertyMetadata(object defaultValue, PropertyChangedCallback propertyChangedCallback) 
            : base(defaultValue, propertyChangedCallback) { }
    }

    /// <summary>
    /// Represents a callback for when a dependency property value changes.
    /// </summary>
    public delegate void PropertyChangedCallback(DependencyObject d, DependencyPropertyChangedEventArgs e);

    /// <summary>
    /// Provides data for dependency property changed events.
    /// </summary>
    public class DependencyPropertyChangedEventArgs : System.EventArgs {
        /// <summary>
        /// Gets the dependency property that changed.
        /// </summary>
        public DependencyProperty Property { get; set; }

        /// <summary>
        /// Gets the old value of the property.
        /// </summary>
        public object OldValue { get; set; }

        /// <summary>
        /// Gets the new value of the property.
        /// </summary>
        public object NewValue { get; set; }
    }
}

