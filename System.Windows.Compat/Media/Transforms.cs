#region "copyright"

/*
    Copyright © 2025 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

namespace System.Windows.Media {
    /// <summary>
    /// Represents a transform that translates (moves) an object.
    /// </summary>
    public class TranslateTransform : Transform {
        public double X { get; set; }
        public double Y { get; set; }
    }

    /// <summary>
    /// Represents formatted text for rendering.
    /// </summary>
    public class FormattedText {
        public string Text { get; set; }
        public System.Globalization.CultureInfo Culture { get; set; }
        public System.Windows.Media.FontFamily FontFamily { get; set; }
        public double FontSize { get; set; }
        public System.Windows.Media.Brush Foreground { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }

        public FormattedText(string text, System.Globalization.CultureInfo culture, 
            System.Windows.FlowDirection flowDirection, System.Windows.Media.Typeface typeface, 
            double emSize, System.Windows.Media.Brush foreground) {
            Text = text;
            Culture = culture;
            FontSize = emSize;
            Foreground = foreground;
            CalculateDimensions();
        }

        public FormattedText(string text, System.Globalization.CultureInfo culture, 
            System.Windows.FlowDirection flowDirection, System.Windows.Media.Typeface typeface, 
            double emSize, System.Windows.Media.Brush foreground, double pixelsPerDip) {
            Text = text;
            Culture = culture;
            FontSize = emSize;
            Foreground = foreground;
            CalculateDimensions();
        }

        private void CalculateDimensions() {
            Width = Text?.Length * FontSize / 2 ?? 0;
            Height = FontSize;
        }

        public System.Windows.Size Measure() {
            return new System.Windows.Size(Width, Height);
        }
    }
}
