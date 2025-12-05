#region "copyright"

/*
    Copyright © 2025 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

namespace System.Windows.Documents {
    /// <summary>
    /// Represents an adorner element that decorates another visual element.
    /// </summary>
    public abstract class Adorner : System.Windows.FrameworkElement {
        public Adorner(System.Windows.UIElement adornedElement) {
            AdornedElement = adornedElement;
        }

        public System.Windows.UIElement AdornedElement { get; set; }

        protected virtual void OnRender(System.Windows.Media.DrawingContext drawingContext) {
        }

        protected virtual System.Windows.Size MeasureOverride(System.Windows.Size constraint) {
            return constraint;
        }

        protected virtual System.Windows.Size ArrangeOverride(System.Windows.Size finalSize) {
            return finalSize;
        }
    }
}

namespace System.Windows.Media.Animation {
    /// <summary>
    /// Base class for animations.
    /// </summary>
    public abstract class AnimationTimeline {
        public TimeSpan? BeginTime { get; set; }
        public TimeSpan Duration { get; set; }
    }

    /// <summary>
    /// Animates the value of a double property between two target values using linear interpolation over a specified Duration.
    /// </summary>
    public class DoubleAnimation : AnimationTimeline {
        public double From { get; set; }
        public double To { get; set; }

        public DoubleAnimation() {
        }

        public DoubleAnimation(double toValue, TimeSpan duration) {
            To = toValue;
            Duration = duration;
        }

        public DoubleAnimation(double fromValue, double toValue, TimeSpan duration) {
            From = fromValue;
            To = toValue;
            Duration = duration;
        }
    }

    /// <summary>
    /// Animates the value of a System.Windows.Point property between two target values using linear interpolation over a specified Duration.
    /// </summary>
    public class PointAnimation : AnimationTimeline {
        public Point? From { get; set; }
        public Point? To { get; set; }

        public PointAnimation() {
        }

        public PointAnimation(Point toValue, TimeSpan duration) {
            To = toValue;
            Duration = duration;
        }

        public PointAnimation(Point fromValue, Point toValue, TimeSpan duration) {
            From = fromValue;
            To = toValue;
            Duration = duration;
        }
    }

    /// <summary>
    /// Animates the value of a System.Windows.Rect property between two target values using linear interpolation over a specified Duration.
    /// </summary>
    public class RectAnimation : AnimationTimeline {
        public Rect? From { get; set; }
        public Rect? To { get; set; }

        public RectAnimation() {
        }

        public RectAnimation(Rect toValue, TimeSpan duration) {
            To = toValue;
            Duration = duration;
        }

        public RectAnimation(Rect fromValue, Rect toValue, TimeSpan duration) {
            From = fromValue;
            To = toValue;
            Duration = duration;
        }
    }

    /// <summary>
    /// Represents the collection of animations for a Storyboard.
    /// </summary>
    public class Timeline {
        public TimeSpan? BeginTime { get; set; }
        public TimeSpan Duration { get; set; }
    }

    /// <summary>
    /// A container for animations on a particular object, used in code-behind.
    /// </summary>
    public class Storyboard {
        public void Begin(FrameworkElement targetElement) {
        }

        public void Stop(FrameworkElement targetElement) {
        }
    }
}

namespace System.Windows.Media.Effects {
    /// <summary>
    /// Base class for effect types.
    /// </summary>
    public abstract class Effect : DependencyObject {
        public static readonly DependencyProperty OpacityProperty = DependencyProperty.Register(
            nameof(Opacity), typeof(double), typeof(Effect), new PropertyMetadata(1.0));

        public double Opacity {
            get => (double)GetValue(OpacityProperty);
            set => SetValue(OpacityProperty, value);
        }
    }

    /// <summary>
    /// Represents a blur effect.
    /// </summary>
    public class BlurEffect : Effect {
        public static readonly DependencyProperty RadiusProperty = DependencyProperty.Register(
            nameof(Radius), typeof(double), typeof(BlurEffect), new PropertyMetadata(0.0));

        public double Radius {
            get => (double)GetValue(RadiusProperty);
            set => SetValue(RadiusProperty, value);
        }
    }

    /// <summary>
    /// Represents a drop shadow effect.
    /// </summary>
    public class DropShadowEffect : Effect {
        public static readonly DependencyProperty BlurRadiusProperty = DependencyProperty.Register(
            nameof(BlurRadius), typeof(double), typeof(DropShadowEffect), new PropertyMetadata(5.0));

        public static readonly DependencyProperty ShadowDepthProperty = DependencyProperty.Register(
            nameof(ShadowDepth), typeof(double), typeof(DropShadowEffect), new PropertyMetadata(5.0));

        public double BlurRadius {
            get => (double)GetValue(BlurRadiusProperty);
            set => SetValue(BlurRadiusProperty, value);
        }

        public double ShadowDepth {
            get => (double)GetValue(ShadowDepthProperty);
            set => SetValue(ShadowDepthProperty, value);
        }
    }
}

