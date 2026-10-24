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
        public static Color Green => Color.FromRgb(0, 128, 0);
        public static Color Lime => Color.FromRgb(0, 255, 0);
        public static Color Blue => Color.FromRgb(0, 0, 255);
        public static Color Yellow => Color.FromRgb(255, 255, 0);
        public static Color Cyan => Color.FromRgb(0, 255, 255);
        public static Color DarkCyan => Color.FromRgb(0, 139, 139);
        public static Color Magenta => Color.FromRgb(255, 0, 255);
        public static Color Gray => Color.FromRgb(128, 128, 128);
        public static Color Orange => Color.FromRgb(255, 165, 0);
        public static Color Purple => Color.FromRgb(128, 0, 128);
        public static Color Pink => Color.FromRgb(255, 192, 203);
        public static Color Brown => Color.FromRgb(165, 42, 42);
        public static Color GreenYellow => Color.FromRgb(173, 255, 47);
        public static Color LightGreen => Color.FromRgb(144, 238, 144);
        public static Color YellowGreen => Color.FromRgb(154, 205, 50);
        public static Color LightBlue => Color.FromRgb(173, 216, 230);
        public static Color Salmon => Color.FromRgb(250, 128, 114);
    }

    public abstract class ImageSource : IDisposable {
        public abstract void Dispose();

        /// <summary>
        /// Natural width in device-independent units. Double rather than int to match WPF, where
        /// a BitmapSource's Width is its pixel width scaled by DPI.
        /// </summary>
        public abstract double Width { get; }

        public abstract double Height { get; }

        /// <summary>
        /// Whether <see cref="Freeze"/> has been called. Freezing is a no-op here - nothing
        /// enforces immutability - so this only reports whether a caller asked for it.
        /// </summary>
        public bool IsFrozen { get; protected set; }

        public virtual void Freeze() {
            IsFrozen = true;
        }
    }

    public class GeometryGroup : Geometry {
        public GeometryCollection Children { get; set; } = new GeometryCollection();

        protected override Freezable CreateInstanceCore() {
            return new GeometryGroup();
        }
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

        public PathFigure(Point startPoint, PathSegmentCollection segments, bool isClosed) {
            StartPoint = startPoint;
            Segments = segments;
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

    public class PathSegmentCollection : System.Collections.ObjectModel.ObservableCollection<PathSegment> {
        public PathSegmentCollection() : base() { }
        public PathSegmentCollection(int capacity) : base() { }
        public PathSegmentCollection(System.Collections.Generic.IEnumerable<PathSegment> collection) : base(collection) { }

        public void Freeze() { }
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

        protected override Freezable CreateInstanceCore() {
            return new PathGeometry();
        }
    }

    public class LineGeometry : Geometry {
        protected override Freezable CreateInstanceCore() {
            return new LineGeometry();
        }
    }

    public class EllipseGeometry : Geometry {
        protected override Freezable CreateInstanceCore() {
            return new EllipseGeometry();
        }
    }

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
        internal string FormatName { get; set; }

        public static PixelFormat Bgr24 { get; } = new PixelFormat { BitsPerPixel = 24, FormatName = "Bgr24" };
        public static PixelFormat Bgra32 { get; } = new PixelFormat { BitsPerPixel = 32, FormatName = "Bgra32" };
        public static PixelFormat Gray16 { get; } = new PixelFormat { BitsPerPixel = 16, FormatName = "Gray16" };
        public static PixelFormat Gray8 { get; } = new PixelFormat { BitsPerPixel = 8, FormatName = "Gray8" };

        // Equality operators — compare by format name to distinguish formats with the same bpp
        public static bool operator ==(PixelFormat left, PixelFormat right) {
            if (ReferenceEquals(left, right)) return true;
            if (left is null || right is null) return false;
            if (left.FormatName != null && right.FormatName != null)
                return left.FormatName == right.FormatName;
            return left.BitsPerPixel == right.BitsPerPixel;
        }

        public static bool operator !=(PixelFormat left, PixelFormat right) => !(left == right);

        public override bool Equals(object obj) => obj is PixelFormat pf && this == pf;

        public override int GetHashCode() => (FormatName ?? BitsPerPixel.ToString()).GetHashCode();

        // Implicit conversion to OpenCV MatType — uses FormatName for precise mapping
        public static implicit operator OpenCvSharp.MatType(PixelFormat pf) {
            return pf.FormatName switch {
                "Gray8" or "Indexed8" => OpenCvSharp.MatType.CV_8UC1,
                "Gray16" => OpenCvSharp.MatType.CV_16UC1,
                "Bgr24" => OpenCvSharp.MatType.CV_8UC3,
                "Bgra32" or "Pbgra32" => OpenCvSharp.MatType.CV_8UC4,
                "Bgr32" => OpenCvSharp.MatType.CV_8UC4,
                "Bgr565" => OpenCvSharp.MatType.CV_16UC1,
                "Rgb48" => OpenCvSharp.MatType.CV_16UC3,
                _ => pf.BitsPerPixel switch {
                    8 => OpenCvSharp.MatType.CV_8UC1,
                    16 => OpenCvSharp.MatType.CV_16UC1,
                    24 => OpenCvSharp.MatType.CV_8UC3,
                    32 => OpenCvSharp.MatType.CV_8UC4,
                    48 => OpenCvSharp.MatType.CV_16UC3,
                    _ => OpenCvSharp.MatType.CV_8UC3
                }
            };
        }

        public override string ToString() => FormatName ?? $"PixelFormat({BitsPerPixel}bpp)";
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

        public void Freeze() { }

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
        public static SolidColorBrush Transparent { get; } = new SolidColorBrush(Colors.Transparent);
        public static SolidColorBrush Black { get; } = new SolidColorBrush(Colors.Black);
        public static SolidColorBrush White { get; } = new SolidColorBrush(Colors.White);
        public static SolidColorBrush Red { get; } = new SolidColorBrush(Colors.Red);
        public static SolidColorBrush Green { get; } = new SolidColorBrush(Colors.Green);
        public static SolidColorBrush Blue { get; } = new SolidColorBrush(Colors.Blue);
        public static SolidColorBrush Yellow { get; } = new SolidColorBrush(Colors.Yellow);
        public static SolidColorBrush Cyan { get; } = new SolidColorBrush(Colors.Cyan);
        public static SolidColorBrush Magenta { get; } = new SolidColorBrush(Colors.Magenta);
        public static SolidColorBrush Gray { get; } = new SolidColorBrush(Colors.Gray);
        public static SolidColorBrush Orange { get; } = new SolidColorBrush(Colors.Orange);
        public static SolidColorBrush Purple { get; } = new SolidColorBrush(Colors.Purple);
        public static SolidColorBrush Pink { get; } = new SolidColorBrush(Colors.Pink);
        public static SolidColorBrush Brown { get; } = new SolidColorBrush(Colors.Brown);
        public static SolidColorBrush LightGreen { get; } = new SolidColorBrush(Colors.LightGreen);
        public static SolidColorBrush Salmon { get; } = new SolidColorBrush(Colors.Salmon);
    }

    public static class PixelFormats {
        public static PixelFormat Bgr24 { get; } = PixelFormat.Bgr24;
        public static PixelFormat Bgra32 { get; } = PixelFormat.Bgra32;
        public static PixelFormat Gray16 { get; } = PixelFormat.Gray16;
        public static PixelFormat Gray8 { get; } = PixelFormat.Gray8;
        public static PixelFormat Rgb48 { get; } = new PixelFormat { BitsPerPixel = 48, FormatName = "Rgb48" };
        public static PixelFormat Bgr32 { get; } = new PixelFormat { BitsPerPixel = 32, FormatName = "Bgr32" };
        public static PixelFormat Pbgra32 { get; } = new PixelFormat { BitsPerPixel = 32, FormatName = "Pbgra32" };
        public static PixelFormat Indexed8 { get; } = new PixelFormat { BitsPerPixel = 8, FormatName = "Indexed8" };
        public static PixelFormat Bgr565 { get; } = new PixelFormat { BitsPerPixel = 16, FormatName = "Bgr565" };
        public static PixelFormat Default => Bgra32;
    }

    /// <summary>
    /// Drawing class for rendering images
    /// </summary>
    public abstract class Drawing {
        public bool CanFreeze => true;
        public void Freeze() { }

        /// <summary>
        /// Axis-aligned bounds of the drawing's content.
        /// </summary>
        public virtual Rect Bounds => default;
    }

    /// <summary>
    /// DrawingGroup for compositing multiple drawings
    /// </summary>
    public class DrawingGroup : Drawing {
        public System.Collections.Generic.List<Drawing> Children { get; set; } = new System.Collections.Generic.List<Drawing>();

        /// <summary>
        /// Clip applied to the group's content. Recorded only - nothing in this shim rasterizes
        /// a DrawingGroup, so the clip is never enforced.
        /// </summary>
        public Geometry ClipGeometry { get; set; }

        /// <summary>
        /// Operations recorded through <see cref="Open"/>, in issue order. Retained so the
        /// composition a caller described can still be inspected, even though it is not drawn.
        /// </summary>
        internal System.Collections.Generic.List<DrawingOperation> Operations { get; } = new System.Collections.Generic.List<DrawingOperation>();

        /// <summary>
        /// Opens the group for recording, matching WPF's DrawingGroup.Open(): the returned
        /// context replaces the group's existing content, and the recording is committed when
        /// the context is disposed.
        /// </summary>
        public DrawingContext Open() {
            Children.Clear();
            Operations.Clear();
            return new DrawingContext(this);
        }

        /// <summary>
        /// A rectangular clip pins the bounds outright, which is the usual case for a group used
        /// as a fixed-size composition surface. Otherwise fall back to the union of whatever
        /// content the group holds.
        /// </summary>
        public override Rect Bounds {
            get {
                if (ClipGeometry is RectangleGeometry clip) {
                    return clip.Rect;
                }

                Rect bounds = Rect.Empty;
                foreach (Drawing child in Children) {
                    bounds = Union(bounds, child.Bounds);
                }
                foreach (DrawingOperation operation in Operations) {
                    bounds = Union(bounds, operation.Rect);
                }
                return bounds.IsEmpty ? default : bounds;
            }
        }

        private static Rect Union(Rect first, Rect second) {
            if (second.Width <= 0 || second.Height <= 0) {
                return first;
            }
            if (first.IsEmpty) {
                return second;
            }
            double left = Math.Min(first.Left, second.Left);
            double top = Math.Min(first.Top, second.Top);
            double right = Math.Max(first.Right, second.Right);
            double bottom = Math.Max(first.Bottom, second.Bottom);
            return new Rect(left, top, right - left, bottom - top);
        }
    }

    /// <summary>
    /// An ImageSource whose content is a vector <see cref="Drawing"/>.
    ///
    /// Headless never rasterizes one: it exists so vector composition paths type-check and can
    /// round-trip the Drawing they built. Anything that needs actual pixels wants a
    /// BitmapSource, not this.
    /// </summary>
    public class DrawingImage : ImageSource {

        public DrawingImage() { }

        public DrawingImage(Drawing drawing) {
            Drawing = drawing;
        }

        public Drawing Drawing { get; set; }

        public override double Width => Drawing?.Bounds.Width ?? 0;

        public override double Height => Drawing?.Bounds.Height ?? 0;

        public override void Dispose() {
            // Nothing unmanaged - the Drawing is a plain managed object graph.
        }
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

        public override Rect Bounds => Rect;
    }

    /// <summary>
    /// RenderOptions for controlling rendering quality
    /// </summary>
    public static class RenderOptions {
        /// <summary>
        /// Selects the process-wide render mode. Defaults to Default as in WPF, so callers that
        /// want the software compositing path have to opt into it the same way they do upstream.
        /// </summary>
        public static System.Windows.Interop.RenderMode ProcessRenderMode { get; set; } = System.Windows.Interop.RenderMode.Default;

        public static void SetBitmapScalingMode(Drawing drawing, BitmapScalingMode mode) {
            // Stub - no-op in headless mode
        }

        /// <summary>
        /// WPF's actual signature takes a DependencyObject. The shim's Drawing does not derive
        /// from DependencyObject, so both targets need their own overload.
        /// </summary>
        public static void SetBitmapScalingMode(DependencyObject target, BitmapScalingMode mode) {
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
    public abstract class Geometry : System.Windows.Freezable {
    }

    /// <summary>
    /// Represents a rectangular geometry.
    /// </summary>
    public class RectangleGeometry : Geometry {

        public RectangleGeometry() { }

        public RectangleGeometry(Rect rect) {
            Rect = rect;
        }

        public Rect Rect { get; set; }

        protected override Freezable CreateInstanceCore() {
            return new RectangleGeometry();
        }
    }
}
