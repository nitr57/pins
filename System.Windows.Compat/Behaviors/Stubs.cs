#region "copyright"

/*
    Copyright © 2025 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

namespace Microsoft.Xaml.Behaviors {
    /// <summary>
    /// Base class for behaviors attached to elements.
    /// </summary>
    public abstract class Behavior : System.Windows.DependencyObject {
        public System.Windows.DependencyObject AssociatedObject { get; set; }

        protected virtual void OnAttached() {
        }

        protected virtual void OnDetaching() {
        }

        public void Attach(System.Windows.DependencyObject obj) {
            AssociatedObject = obj;
            OnAttached();
        }

        public void Detach() {
            OnDetaching();
            AssociatedObject = null;
        }
    }

    /// <summary>
    /// Generic base class for behaviors attached to elements.
    /// </summary>
    public abstract class Behavior<T> : Behavior where T : System.Windows.DependencyObject {
        public new T AssociatedObject { get; set; }

        protected virtual void OnAttached() {
        }

        protected virtual void OnDetaching() {
        }
    }
}

namespace System.Windows {
    /// <summary>
    /// Specifies that an attached property is browsable for a specific type.
    /// </summary>
    [System.AttributeUsage(System.AttributeTargets.Method)]
    public sealed class AttachedPropertyBrowsableForTypeAttribute : System.Attribute {
        public System.Type TargetType { get; set; }

        public AttachedPropertyBrowsableForTypeAttribute(System.Type targetType) {
            TargetType = targetType;
        }
    }

    /// <summary>
    /// Enum for hit test result behavior
    /// </summary>
    public enum HitTestResultBehavior {
        Continue,
        Stop
    }

    /// <summary>
    /// Enum for hit test filter behavior
    /// </summary>
    public enum HitTestFilterBehavior {
        Continue,
        Skip
    }

    /// <summary>
    /// Provides access to attached behaviors.
    /// </summary>
    public static class Interaction {
        public static readonly System.Windows.DependencyProperty BehaviorsProperty = 
            System.Windows.DependencyProperty.RegisterAttached(
                "Behaviors",
                typeof(BehaviorCollection),
                typeof(Interaction),
                new System.Windows.PropertyMetadata(null));

        public static BehaviorCollection GetBehaviors(System.Windows.DependencyObject obj) {
            var collection = obj?.GetValue(BehaviorsProperty) as BehaviorCollection;
            if (collection == null) {
                collection = new BehaviorCollection();
                obj?.SetValue(BehaviorsProperty, collection);
            }
            return collection;
        }

        public static void SetBehaviors(System.Windows.DependencyObject obj, BehaviorCollection value) {
            obj?.SetValue(BehaviorsProperty, value);
        }
    }

    /// <summary>
    /// Collection of behaviors
    /// </summary>
    public class BehaviorCollection : System.Collections.ObjectModel.ObservableCollection<Microsoft.Xaml.Behaviors.Behavior> {
    }
}

namespace System.Windows.Controls {
    /// <summary>
    /// Represents a web browser control.
    /// </summary>
    public class WebBrowser : FrameworkElement {
        public string Source { get; set; }
        public object Document { get; set; }

        public void NavigateToString(string html) {
        }
    }
}
