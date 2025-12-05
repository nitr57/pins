#region "copyright"

/*
    Copyright © 2025 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

namespace System.Windows.Input {
    /// <summary>
    /// Represents the state of a mouse button.
    /// </summary>
    public enum MouseButtonState {
        Released,
        Pressed
    }

    /// <summary>
    /// Represents a mouse input device.
    /// </summary>
    public class MouseDevice {
        public int Timestamp { get; set; }
        public System.Windows.Input.Cursor OverrideCursor { get; set; }
    }

    /// <summary>
    /// Provides data for mouse input events.
    /// </summary>
    public class MouseEventArgs : System.Windows.RoutedEventArgs {
        public MouseDevice MouseDevice { get; set; }
        public int Timestamp { get; set; }
        public MouseButtonState LeftButton { get; set; } = MouseButtonState.Released;
        public MouseButtonState RightButton { get; set; } = MouseButtonState.Released;
        public MouseButtonState MiddleButton { get; set; } = MouseButtonState.Released;
        public MouseButtonState XButton1 { get; set; } = MouseButtonState.Released;
        public MouseButtonState XButton2 { get; set; } = MouseButtonState.Released;
        public System.Windows.Input.StylusDevice StylusDevice { get; set; }
        public bool Handled { get; set; }

        public MouseEventArgs() {
        }

        public MouseEventArgs(MouseDevice mouseDevice, int timestamp) {
            MouseDevice = mouseDevice;
            Timestamp = timestamp;
        }

        public System.Windows.Point GetPosition(System.Windows.IInputElement relativeTo) {
            return new System.Windows.Point(0, 0);
        }
    }

    /// <summary>
    /// Provides data for mouse button input events.
    /// </summary>
    public class MouseButtonEventArgs : MouseEventArgs {
        public MouseButton Button { get; set; }
        public MouseButton ChangedButton { get; set; }
        public int ClickCount { get; set; }

        public MouseButtonEventArgs() {
        }

        public MouseButtonEventArgs(MouseDevice mouseDevice, int timestamp, MouseButton button) {
            MouseDevice = mouseDevice;
            Timestamp = timestamp;
            Button = button;
            ChangedButton = button;
        }

        public System.Windows.Point GetPosition(System.Windows.IInputElement relativeTo) {
            return new System.Windows.Point(0, 0);
        }
    }

    /// <summary>
    /// Provides data for touch input events.
    /// </summary>
    public class TouchEventArgs : System.Windows.RoutedEventArgs {
        public TouchDevice TouchDevice { get; set; }
        public bool Handled { get; set; }
    }

    /// <summary>
    /// Provides data for stylus input events.
    /// </summary>
    public class StylusEventArgs : System.Windows.RoutedEventArgs {
        public System.Windows.Input.StylusDevice StylusDevice { get; set; }

        public System.Windows.Point GetPosition(System.Windows.IInputElement relativeTo) {
            return new System.Windows.Point(0, 0);
        }
    }

    /// <summary>
    /// Provides data for stylus down input events.
    /// </summary>
    public class StylusDownEventArgs : StylusEventArgs {
    }

    /// <summary>
    /// Provides data for mouse wheel input events.
    /// </summary>
    public class MouseWheelEventArgs : MouseEventArgs {
        public int Delta { get; set; }
        public bool Handled { get; set; }
        public System.Windows.RoutedEvent RoutedEvent { get; set; }

        public MouseWheelEventArgs() {
        }

        public MouseWheelEventArgs(MouseDevice mouseDevice, int timestamp, int delta) {
            MouseDevice = mouseDevice;
            Timestamp = timestamp;
            Delta = delta;
        }
    }

    /// <summary>
    /// Provides data for keyboard input events.
    /// </summary>
    public class KeyEventArgs : System.Windows.RoutedEventArgs {
        public System.Windows.Input.Key Key { get; set; }
        public bool Handled { get; set; }
    }

    /// <summary>
    /// Represents a keyboard key.
    /// </summary>
    public enum Key {
        None,
        Cancel,
        Back,
        Tab,
        Enter,
        Escape,
        Space,
        Delete,
        D0, D1, D2, D3, D4, D5, D6, D7, D8, D9,
        A, B, C, D, E, F, G, H, I, J, K, L, M, N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
        LCtrl,
        RCtrl,
        LShift,
        RShift,
        LAlt,
        RAlt,
        LeftAlt = LAlt,
        RightAlt = RAlt,
        LeftCtrl = LCtrl,
        RightCtrl = RCtrl,
        LeftShift = LShift,
        RightShift = RShift
    }

    /// <summary>
    /// Represents a mouse button.
    /// </summary>
    public enum MouseButton {
        Left,
        Middle,
        Right,
        XButton1,
        XButton2
    }

    /// <summary>
    /// Represents a touch device.
    /// </summary>
    public class TouchDevice {
        public TouchPoint GetTouchPoint(System.Windows.IInputElement relativeTo) {
            return new TouchPoint();
        }
    }

    /// <summary>
    /// Represents a point generated by a touch device.
    /// </summary>
    public class TouchPoint {
        public System.Windows.Point Position { get; set; }
    }

    /// <summary>
    /// Represents a stylus device.
    /// </summary>
    public class StylusDevice {
        public int Id { get; set; }
        public TabletDevice TabletDevice { get; set; }
    }

    /// <summary>
    /// Represents a tablet device.
    /// </summary>
    public class TabletDevice {
        public TabletDeviceType Type { get; set; }
    }

    /// <summary>
    /// Specifies the type of tablet device.
    /// </summary>
    public enum TabletDeviceType {
        Stylus,
        Touch
    }

    /// <summary>
    /// Provides access to keyboard input information.
    /// </summary>
    public static class Keyboard {
        public static bool IsKeyDown(Key key) {
            return false;
        }

        public static KeyStates GetKeyStates(Key key) {
            return KeyStates.None;
        }
    }

    /// <summary>
    /// Represents keyboard key states.
    /// </summary>
    public enum KeyStates {
        None,
        Down,
        Toggled
    }
    public static class Mouse {
        public static MouseDevice PrimaryDevice { get; } = new MouseDevice();
        public static System.Windows.Input.Cursor OverrideCursor { get; set; }

        public static System.Windows.Point GetPosition(System.Windows.IInputElement relativeTo) {
            return new System.Windows.Point(0, 0);
        }
    }

    /// <summary>
    /// Represents a mouse cursor.
    /// </summary>
    public class Cursor {
    }

    /// <summary>
    /// Provides predefined cursor values.
    /// </summary>
    public static class Cursors {
        public static Cursor Arrow { get; } = new Cursor();
        public static Cursor Cross { get; } = new Cursor();
        public static Cursor SizeNWSE { get; } = new Cursor();
        public static Cursor SizeNESW { get; } = new Cursor();
        public static Cursor SizeWE { get; } = new Cursor();
        public static Cursor SizeNS { get; } = new Cursor();
        public static Cursor SizeAll { get; } = new Cursor();
        public static Cursor Hand { get; } = new Cursor();
        public static Cursor Wait { get; } = new Cursor();
    }
}

namespace System.Windows {
    /// <summary>
    /// Represents an element in the UI tree that can measure and arrange child elements.
    /// </summary>
    public class FrameworkElement : UIElement {
        public double Width { get; set; }
        public double Height { get; set; }
        public double ActualWidth { get; set; }
        public double ActualHeight { get; set; }
        public object Parent { get; set; }
        public bool IsMouseCaptured { get; set; }
        public object DataContext { get; set; }
        public Media.Effects.Effect Effect { get; set; }
        public Media.Transform RenderTransform { get; set; }
        public event EventHandler<System.Windows.Input.KeyEventArgs> KeyDown;

        public void CaptureTouch(System.Windows.Input.TouchDevice touchDevice) {
        }

        public void ReleaseTouchCapture(System.Windows.Input.TouchDevice touchDevice) {
        }

        public void CaptureMouse() {
        }

        public void ReleaseMouseCapture() {
        }

        public void CaptureStylus() {
        }

        public void ReleaseStylusCapture() {
        }

        public event EventHandler<System.Windows.Input.MouseEventArgs> MouseMove;
        public event EventHandler<System.Windows.Input.MouseEventArgs> MouseLeave;
        public event EventHandler<System.Windows.Input.MouseButtonEventArgs> MouseLeftButtonDown;
        public event EventHandler<System.Windows.Input.MouseButtonEventArgs> MouseLeftButtonUp;
        public event EventHandler<System.Windows.Input.TouchEventArgs> TouchDown;
        public event EventHandler<System.Windows.Input.TouchEventArgs> TouchMove;
        public event EventHandler<System.Windows.Input.TouchEventArgs> TouchUp;
        public event EventHandler<System.Windows.Input.StylusEventArgs> StylusDown;
        public event EventHandler<System.Windows.Input.StylusEventArgs> StylusMove;
        public event EventHandler<System.Windows.Input.StylusEventArgs> StylusUp;

        public object FindResource(object resourceKey) {
            // Try to find resource in Application resources
            var app = Application.Current;
            if (app?.Resources != null) {
                if (app.Resources.TryGetValue(resourceKey.ToString(), out var resource)) {
                    return resource;
                }
            }
            return null;
        }

        public object TryFindResource(object resourceKey) {
            // Try to find resource in Application resources, return null if not found
            var app = Application.Current;
            if (app?.Resources != null) {
                if (app.Resources.TryGetValue(resourceKey.ToString(), out var resource)) {
                    return resource;
                }
            }
            return null;
        }

        public Point TranslatePoint(Point point, UIElement relativeTo) {
            // In headless mode, just return the point as-is
            // This is used to convert coordinates between UI elements
            return point;
        }
    }

    /// <summary>
    /// Represents an element in the UI tree.
    /// </summary>
    public class UIElement : DependencyObject, System.Windows.IInputElement {
        public static readonly RoutedEvent MouseWheelEvent = new RoutedEvent();
        public static readonly RoutedEvent PreviewMouseWheelEvent = new RoutedEvent();
        public static readonly RoutedEvent MouseLeaveEvent = new RoutedEvent();
        public static readonly RoutedEvent MouseMoveEvent = new RoutedEvent();
        public static readonly RoutedEvent MouseEnterEvent = new RoutedEvent();
        public bool IsEnabled { get; set; } = true;
        public bool IsHitTestVisible { get; set; } = true;

        public event EventHandler<System.Windows.Input.MouseEventArgs> MouseEnter;
        public event EventHandler<System.Windows.Input.MouseEventArgs> MouseLeave;
        public event EventHandler<System.Windows.Input.MouseEventArgs> MouseMove;
        public event EventHandler<System.Windows.Input.MouseButtonEventArgs> MouseDown;
        public event EventHandler<System.Windows.Input.MouseButtonEventArgs> MouseUp;
        public event EventHandler<System.Windows.Input.MouseButtonEventArgs> MouseLeftButtonDown;
        public event EventHandler<System.Windows.Input.MouseButtonEventArgs> MouseLeftButtonUp;
        public event EventHandler<System.Windows.Input.MouseWheelEventArgs> MouseWheel;
        public event EventHandler<System.Windows.Input.MouseWheelEventArgs> PreviewMouseWheel;
        public event EventHandler<System.Windows.Input.TouchEventArgs> TouchDown;
        public event EventHandler<System.Windows.Input.TouchEventArgs> TouchMove;
        public event EventHandler<System.Windows.Input.TouchEventArgs> TouchUp;
        public event EventHandler<System.Windows.Input.StylusDownEventArgs> StylusDown;
        public event EventHandler<System.Windows.Input.StylusEventArgs> StylusMove;
        public event EventHandler<System.Windows.Input.StylusEventArgs> StylusUp;

        public void RaiseEvent(RoutedEventArgs e) {
        }

        public void InvalidateVisual() {
            // Stub for invalidating the visual in headless mode
        }
    }
}
