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
    /// Enum for specifying how content is stretched.
    /// </summary>
    public enum Stretch {
        None,
        Fill,
        Uniform,
        UniformToFill
    }

    public static class Colors {
        public static Color Transparent => Color.FromArgb(0, 0, 0, 0);
        public static Color Black => Color.FromRgb(0, 0, 0);
        public static Color White => Color.FromRgb(255, 255, 255);
        public static Color Red => Color.FromRgb(255, 0, 0);
        public static Color Green => Color.FromRgb(0, 255, 0);
        public static Color Blue => Color.FromRgb(0, 0, 255);
        public static Color Yellow => Color.FromRgb(255, 255, 0);
        public static Color Cyan => Color.FromRgb(0, 255, 255);
        public static Color Magenta => Color.FromRgb(255, 0, 255);
        public static Color Gray => Color.FromRgb(128, 128, 128);
        public static Color Orange => Color.FromRgb(255, 165, 0);
        public static Color Purple => Color.FromRgb(128, 0, 128);
        public static Color Pink => Color.FromRgb(255, 192, 203);
        public static Color Brown => Color.FromRgb(165, 42, 42);
    }

    public abstract class ImageSource { }

    public class GeometryGroup : Geometry {
        public GeometryCollection Children { get; set; } = new GeometryCollection();
    }

    public class GeometryCollection : System.Collections.Generic.List<Geometry> { }

    public class PointCollection : System.Collections.Generic.List<Point> {
        public PointCollection() : base() { }
        public PointCollection(int capacity) : base(capacity) { }
        public PointCollection(System.Collections.Generic.IEnumerable<Point> collection) : base(collection) { }
    }

    public class PathFigure {
        public Point StartPoint { get; set; }
        public PathSegmentCollection Segments { get; set; } = new PathSegmentCollection();
        public bool IsClosed { get; set; }
        public bool IsFilled { get; set; } = true;

        public PathFigure() { }

        public PathFigure(Point startPoint, System.Collections.Generic.List<PathSegment> segments, bool isClosed) {
            StartPoint = startPoint;
            Segments = new PathSegmentCollection(segments);
            IsClosed = isClosed;
        }
    }

    public class PathFigureCollection : System.Collections.Generic.List<PathFigure> {
        public PathFigureCollection() : base() { }
        public PathFigureCollection(int capacity) : base(capacity) { }
        public PathFigureCollection(System.Collections.Generic.IEnumerable<PathFigure> collection) : base(collection) { }
    }

    public abstract class PathSegment { }

    public class LineSegment : PathSegment {
        public Point Point { get; set; }
        public bool IsStroked { get; set; } = true;

        public LineSegment() { }

        public LineSegment(Point point, bool isStroked) {
            Point = point;
            IsStroked = isStroked;
        }
    }

    public class PathSegmentCollection : System.Collections.Generic.List<PathSegment> {
        public PathSegmentCollection() : base() { }
        public PathSegmentCollection(int capacity) : base(capacity) { }
        public PathSegmentCollection(System.Collections.Generic.IEnumerable<PathSegment> collection) : base(collection) { }
    }

    /// <summary>
    /// Specifies the fill rule for a path geometry.
    /// </summary>
    public enum FillRule {
        EvenOdd,
        Nonzero
    }

    public class PathGeometry : Geometry {
        public PathFigureCollection Figures { get; set; } = new PathFigureCollection();
        public FillRule FillRule { get; set; } = FillRule.EvenOdd;
    }

    public class LineGeometry : Geometry { }

    public class EllipseGeometry : Geometry { }

    public struct Color {
        public byte A { get; set; }
        public byte R { get; set; }
        public byte G { get; set; }
        public byte B { get; set; }

        public static Color FromArgb(byte a, byte r, byte g, byte b) => new Color { A = a, R = r, G = g, B = b };
        public static Color FromRgb(byte r, byte g, byte b) => FromArgb(255, r, g, b);

        // Implicit conversion to Scalar (BGR order for OpenCV)
        public static implicit operator OpenCvSharp.Scalar(Color color) => new OpenCvSharp.Scalar(color.B, color.G, color.R, color.A);

        // Implicit conversion from Scalar (BGR order from OpenCV)
        public static implicit operator Color(OpenCvSharp.Scalar scalar) => new Color {
            B = (byte)scalar.Val0,
            G = (byte)scalar.Val1,
            R = (byte)scalar.Val2,
            A = (byte)scalar.Val3
        };

        // Equality operators
        public static bool operator ==(Color left, Color right) =>
            left.A == right.A && left.R == right.R && left.G == right.G && left.B == right.B;

        public static bool operator !=(Color left, Color right) => !(left == right);

        public override bool Equals(object obj) => obj is Color color && this == color;

        public override int GetHashCode() => HashCode.Combine(A, R, G, B);
    }

    public class PixelFormat {
        public int BitsPerPixel { get; set; }

        public static PixelFormat Bgr24 => new PixelFormat { BitsPerPixel = 24 };
        public static PixelFormat Bgra32 => new PixelFormat { BitsPerPixel = 32 };
        public static PixelFormat Gray16 => new PixelFormat { BitsPerPixel = 16 };
        public static PixelFormat Gray8 => new PixelFormat { BitsPerPixel = 8 };

        // Equality operators for format comparison
        public static bool operator ==(PixelFormat left, PixelFormat right) {
            if (ReferenceEquals(left, right)) return true;
            if (left is null || right is null) return false;
            return left.BitsPerPixel == right.BitsPerPixel;
        }

        public static bool operator !=(PixelFormat left, PixelFormat right) => !(left == right);

        public override bool Equals(object obj) => obj is PixelFormat pf && this == pf;

        public override int GetHashCode() => BitsPerPixel.GetHashCode();

        // Implicit conversion to OpenCV MatType
        public static implicit operator OpenCvSharp.MatType(PixelFormat pf) {
            // Map based on bits per pixel
            return pf.BitsPerPixel switch {
                8 => OpenCvSharp.MatType.CV_8UC1,   // Gray8
                16 => OpenCvSharp.MatType.CV_16UC1, // Gray16
                24 => OpenCvSharp.MatType.CV_8UC3,  // Bgr24
                32 => OpenCvSharp.MatType.CV_8UC4,  // Bgra32
                48 => OpenCvSharp.MatType.CV_16UC3, // Rgb48
                _ => OpenCvSharp.MatType.CV_8UC3
            };
        }
    }

    /// <summary>
    /// Base brush class
    /// </summary>
    public abstract class Brush { }

    /// <summary>
    /// Solid color brush
    /// </summary>
    public class SolidColorBrush : Brush {
        public Color Color { get; set; }

        public SolidColorBrush() {
            Color = Colors.White;
        }

        public SolidColorBrush(Color color) {
            Color = color;
        }

        // Implicit conversion to Scalar for OpenCV
        public static implicit operator OpenCvSharp.Scalar(SolidColorBrush brush) => brush.Color;
    }

    /// <summary>
    /// Brush that paints with visual content
    /// </summary>
    public class VisualBrush : Brush {
        public object Visual { get; set; }
        public Stretch Stretch { get; set; } = Stretch.Fill;
        public double Opacity { get; set; } = 1.0;

        public VisualBrush() { }

        public VisualBrush(object visual) {
            Visual = visual;
        }
    }

    /// <summary>
    /// Pen for drawing lines and outlines
    /// </summary>
    public class Pen {
        public Brush Brush { get; set; }
        public double Thickness { get; set; }

        public Pen() {
            Brush = new SolidColorBrush(Colors.Black);
            Thickness = 1.0;
        }

        public Pen(Brush brush, double thickness) {
            Brush = brush;
            Thickness = thickness;
        }
    }

    /// <summary>
    /// Static Brushes class with predefined brushes
    /// </summary>
    public static class Brushes {
        public static SolidColorBrush Transparent => new SolidColorBrush(Colors.Transparent);
        public static SolidColorBrush Black => new SolidColorBrush(Colors.Black);
        public static SolidColorBrush White => new SolidColorBrush(Colors.White);
        public static SolidColorBrush Red => new SolidColorBrush(Colors.Red);
        public static SolidColorBrush Green => new SolidColorBrush(Colors.Green);
        public static SolidColorBrush Blue => new SolidColorBrush(Colors.Blue);
        public static SolidColorBrush Yellow => new SolidColorBrush(Colors.Yellow);
        public static SolidColorBrush Cyan => new SolidColorBrush(Colors.Cyan);
        public static SolidColorBrush Magenta => new SolidColorBrush(Colors.Magenta);
        public static SolidColorBrush Gray => new SolidColorBrush(Colors.Gray);
        public static SolidColorBrush Orange => new SolidColorBrush(Colors.Orange);
        public static SolidColorBrush Purple => new SolidColorBrush(Colors.Purple);
        public static SolidColorBrush Pink => new SolidColorBrush(Colors.Pink);
        public static SolidColorBrush Brown => new SolidColorBrush(Colors.Brown);
    }

    public static class PixelFormats {
        public static PixelFormat Bgr24 => PixelFormat.Bgr24;
        public static PixelFormat Bgra32 => PixelFormat.Bgra32;
        public static PixelFormat Gray16 => PixelFormat.Gray16;
        public static PixelFormat Gray8 => PixelFormat.Gray8;
        public static PixelFormat Rgb48 => new PixelFormat { BitsPerPixel = 48 };
        public static PixelFormat Bgr32 => new PixelFormat { BitsPerPixel = 32 };
        public static PixelFormat Pbgra32 => new PixelFormat { BitsPerPixel = 32 };
        public static PixelFormat Indexed8 => new PixelFormat { BitsPerPixel = 8 };
        public static PixelFormat Bgr565 => new PixelFormat { BitsPerPixel = 16 };
        public static PixelFormat Default => Bgra32;
    }

    /// <summary>
    /// Drawing class for rendering images
    /// </summary>
    public abstract class Drawing {
        public bool CanFreeze => true;
        public void Freeze() { }
    }

    /// <summary>
    /// DrawingGroup for compositing multiple drawings
    /// </summary>
    public class DrawingGroup : Drawing {
        public System.Collections.Generic.List<Drawing> Children { get; set; } = new System.Collections.Generic.List<Drawing>();
    }

    /// <summary>
    /// ImageDrawing for drawing an image in a rectangle
    /// </summary>
    public class ImageDrawing : Drawing {
        public ImageSource ImageSource { get; set; }
        public Rect Rect { get; set; }

        public ImageDrawing(ImageSource imageSource, Rect rect) {
            ImageSource = imageSource;
            Rect = rect;
        }
    }

    /// <summary>
    /// RenderOptions for controlling rendering quality
    /// </summary>
    public static class RenderOptions {
        public static void SetBitmapScalingMode(Drawing drawing, BitmapScalingMode mode) {
            // Stub - no-op in headless mode
        }
    }

    /// <summary>
    /// BitmapScalingMode enumeration
    /// </summary>
    public enum BitmapScalingMode {
        Unspecified,
        LowQuality,
        HighQuality,
        Fant,
        Linear,
        NearestNeighbor
    }

    /// <summary>
    /// FontFamily class compatible with WPF API
    /// </summary>
    public class FontFamily {
        private readonly string _familyName;
        
        public FontFamily(string familyName) {
            _familyName = familyName ?? "Arial";
        }

        /// <summary>
        /// Gets the family names as a dictionary (compatible with WPF API)
        /// </summary>
        public System.Collections.Generic.IDictionary<System.Globalization.CultureInfo, string> FamilyNames {
            get {
                var dict = new System.Collections.Generic.Dictionary<System.Globalization.CultureInfo, string>();
                dict[System.Globalization.CultureInfo.InvariantCulture] = _familyName;
                return dict;
            }
        }

        /// <summary>
        /// Gets the family name
        /// </summary>
        public string Source => _familyName;

        public override string ToString() => _familyName;
    }

    /// <summary>
    /// Visual base class (minimal implementation for DPI-aware operations)
    /// </summary>
    public class Visual {
    }

    /// <summary>
    /// Represents a typeface (font family, weight, style, stretch).
    /// </summary>
    public class Typeface {
        public Typeface(FontFamily fontFamily) {
            FontFamily = fontFamily;
        }

        public Typeface(FontFamily fontFamily, FontStyle style, FontWeight weight, FontStretch stretch) {
            FontFamily = fontFamily;
            Style = style;
            Weight = weight;
            Stretch = stretch;
        }

        public Typeface(FontFamily fontFamily, FontStyle style, FontWeight weight, FontStretch stretch, FontFamily fallback) {
            FontFamily = fontFamily;
            Style = style;
            Weight = weight;
            Stretch = stretch;
        }

        public FontFamily FontFamily { get; set; }
        public FontStyle Style { get; set; }
        public FontWeight Weight { get; set; }
        public FontStretch Stretch { get; set; }
    }

    /// <summary>
    /// Represents font style options.
    /// </summary>
    public enum FontStyle {
        Normal,
        Italic,
        Oblique
    }

    /// <summary>
    /// Provides static predefined font styles.
    /// </summary>
    public static class FontStyles {
        public static FontStyle Normal => FontStyle.Normal;
        public static FontStyle Italic => FontStyle.Italic;
        public static FontStyle Oblique => FontStyle.Oblique;
    }

    /// <summary>
    /// Represents font weight options.
    /// </summary>
    public enum FontWeight {
        Thin,
        ExtraLight,
        Light,
        Normal,
        Medium,
        SemiBold,
        Bold,
        ExtraBold,
        Black
    }

    /// <summary>
    /// Provides static predefined font weights.
    /// </summary>
    public static class FontWeights {
        public static FontWeight Thin => FontWeight.Thin;
        public static FontWeight ExtraLight => FontWeight.ExtraLight;
        public static FontWeight Light => FontWeight.Light;
        public static FontWeight Normal => FontWeight.Normal;
        public static FontWeight Medium => FontWeight.Medium;
        public static FontWeight SemiBold => FontWeight.SemiBold;
        public static FontWeight Bold => FontWeight.Bold;
        public static FontWeight ExtraBold => FontWeight.ExtraBold;
        public static FontWeight Black => FontWeight.Black;
    }

    /// <summary>
    /// Represents font stretch options.
    /// </summary>
    public enum FontStretch {
        UltraCondensed,
        ExtraCondensed,
        Condensed,
        SemiCondensed,
        Normal,
        SemiExpanded,
        Expanded,
        ExtraExpanded,
        UltraExpanded
    }

    /// <summary>
    /// Provides static predefined font stretches.
    /// </summary>
    public static class FontStretches {
        public static FontStretch UltraCondensed => FontStretch.UltraCondensed;
        public static FontStretch ExtraCondensed => FontStretch.ExtraCondensed;
        public static FontStretch Condensed => FontStretch.Condensed;
        public static FontStretch SemiCondensed => FontStretch.SemiCondensed;
        public static FontStretch Normal => FontStretch.Normal;
        public static FontStretch SemiExpanded => FontStretch.SemiExpanded;
        public static FontStretch Expanded => FontStretch.Expanded;
        public static FontStretch ExtraExpanded => FontStretch.ExtraExpanded;
        public static FontStretch UltraExpanded => FontStretch.UltraExpanded;
    }

    /// <summary>
    /// Represents a geometric shape.
    /// </summary>
    public abstract class Geometry {
    }

    /// <summary>
    /// Represents a rectangular geometry.
    /// </summary>
    public class RectangleGeometry : Geometry {
        public Rect Rect { get; set; }
    }
}
