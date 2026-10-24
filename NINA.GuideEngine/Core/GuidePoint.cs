// SPDX-License-Identifier: MPL-2.0

namespace NINA.GuideEngine.Core;

/// <summary>
/// A 2D point that can be invalid, mirroring PHD2's PHD_Point semantics.
/// </summary>
public readonly record struct GuidePoint(double X, double Y, bool IsValid = true)
{
    public static GuidePoint Invalid => new(0, 0, false);

    public double DX(GuidePoint p) => X - p.X;

    public double DY(GuidePoint p) => Y - p.Y;

    public double Distance(GuidePoint p) => Math.Sqrt(DX(p) * DX(p) + DY(p) * DY(p));

    public double Distance() => Math.Sqrt(X * X + Y * Y);

    /// <summary>Angle of the vector from <paramref name="p"/> to this point, radians.</summary>
    public double Angle(GuidePoint p) => Math.Atan2(DY(p), DX(p));

    public double Angle() => Math.Atan2(Y, X);

    public static GuidePoint operator +(GuidePoint a, GuidePoint b) => new(a.X + b.X, a.Y + b.Y, a.IsValid && b.IsValid);

    public static GuidePoint operator -(GuidePoint a, GuidePoint b) => new(a.X - b.X, a.Y - b.Y, a.IsValid && b.IsValid);

    public override string ToString() => IsValid ? $"({X:F3}, {Y:F3})" : "(invalid)";
}
