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
    /// <summary>
    /// Represents a popup window that appears above other content.
    /// </summary>
    public class Popup : FrameworkElement {
        public double HorizontalOffset { get; set; }
        public double VerticalOffset { get; set; }
        public IInputElement PlacementTarget { get; set; }
        public Controls.Primitives.PlacementMode Placement { get; set; }
        public UIElement Child { get; set; }
        public bool IsOpen { get; set; }

        public event EventHandler Opened;
        public event EventHandler Closed;
    }
}

namespace System.Windows.Controls {
    /// <summary>
    /// Represents a grid panel that contains rows and columns.
    /// </summary>
    public class Grid : FrameworkElement {
        public System.Collections.Generic.List<RowDefinition> RowDefinitions { get; set; } = new System.Collections.Generic.List<RowDefinition>();
        public System.Collections.Generic.List<ColumnDefinition> ColumnDefinitions { get; set; } = new System.Collections.Generic.List<ColumnDefinition>();
        public System.Collections.Generic.List<UIElement> Children { get; set; } = new System.Collections.Generic.List<UIElement>();
    }

    /// <summary>
    /// Represents a row definition in a grid.
    /// </summary>
    public class RowDefinition {
        public GridLength Height { get; set; }
    }

    /// <summary>
    /// Represents a column definition in a grid.
    /// </summary>
    public class ColumnDefinition {
        public GridLength Width { get; set; }
    }

    /// <summary>
    /// Represents a length value in a grid.
    /// </summary>
    public struct GridLength {
        public double Value { get; set; }
        public GridUnitType UnitType { get; set; }
    }

    /// <summary>
    /// Specifies the unit type of a grid length.
    /// </summary>
    public enum GridUnitType {
        Auto,
        Pixel,
        Star
    }

    /// <summary>
    /// Represents a popup window that appears above other content.
    /// </summary>
    public class Popup : System.Windows.Popup {
    }

    /// <summary>
    /// Represents a control that allows content to be scrolled.
    /// </summary>
    public class ScrollViewer : FrameworkElement {
        public double VerticalOffset { get; set; }
        public double HorizontalOffset { get; set; }
        public double ViewportHeight { get; set; }
        public double ViewportWidth { get; set; }
        public double ExtentHeight { get; set; }
        public double ExtentWidth { get; set; }
        public UIElement Content { get; set; }
        
        public ScrollBarVisibility VerticalScrollBarVisibility { get; set; }
        public ScrollBarVisibility HorizontalScrollBarVisibility { get; set; }

        public void ScrollToVerticalOffset(double offset) {
            VerticalOffset = offset;
        }

        public void ScrollToHorizontalOffset(double offset) {
            HorizontalOffset = offset;
        }
    }

    /// <summary>
    /// Specifies the visibility of a scrollbar.
    /// </summary>
    public enum ScrollBarVisibility {
        Disabled,
        Auto,
        Hidden,
        Visible
    }
    /// <summary>
    /// Represents a menu item control.
    /// </summary>
    public class MenuItem : FrameworkElement {
        public string Header { get; set; }
        public System.Collections.Generic.List<MenuItem> Items { get; set; } = new System.Collections.Generic.List<MenuItem>();
        public System.Windows.Input.ICommand Command { get; set; }
        public object CommandParameter { get; set; }
        public bool IsLoaded { get; set; }
        public bool IsSubmenuOpen { get; set; }

        public event System.Windows.RoutedEventHandler Loaded;
    }
}

namespace System.Windows.Controls.Primitives {
    /// <summary>
    /// Specifies the placement of a popup element.
    /// </summary>
    public enum PlacementMode {
        Absolute,
        Relative,
        Left,
        Right,
        Top,
        Bottom,
        Center,
        Mouse,
        MousePoint,
        Custom
    }
}
