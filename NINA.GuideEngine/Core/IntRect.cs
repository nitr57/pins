// SPDX-License-Identifier: MPL-2.0

namespace NINA.GuideEngine.Core;

/// <summary>Integer rectangle (wxRect semantics: Right = X + Width - 1).</summary>
public readonly record struct IntRect(int X, int Y, int Width, int Height)
{
    public static IntRect Empty => default;

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public int Left => X;

    public int Top => Y;

    public int Right => X + Width - 1;

    public int Bottom => Y + Height - 1;

    public bool Contains(int x, int y) => x >= X && x <= Right && y >= Y && y <= Bottom;

    public bool Contains(double x, double y) => x >= X && x <= Right && y >= Y && y <= Bottom;

    public IntRect Intersect(IntRect o)
    {
        int l = Math.Max(Left, o.Left), t = Math.Max(Top, o.Top);
        int r = Math.Min(Right, o.Right), b = Math.Min(Bottom, o.Bottom);
        return r < l || b < t ? Empty : new IntRect(l, t, r - l + 1, b - t + 1);
    }

    public IntRect Deflate(int d) => new(X + d, Y + d, Width - 2 * d, Height - 2 * d);
}
