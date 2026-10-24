// SPDX-License-Identifier: MPL-2.0

namespace NINA.GuideEngine.Algorithms;

// API CONTRACT.

/// <summary>Per-axis guide algorithm (port of PHD2 GuideAlgorithm). Input/output in mount-axis pixels.</summary>
public interface IGuideAlgorithm
{
    string Name { get; }

    double MinMove { get; set; }

    /// <summary>Returns the correction (pixels) for the measured axis error.</summary>
    double Result(double input);

    /// <summary>
    /// <see cref="Result(double)"/> with the time of the measurement, for model-based algorithms that need the time
    /// between frames. The PHD2 algorithms ignore it.
    /// </summary>
    double Result(double input, DateTimeOffset time) => Result(input);

    /// <summary>
    /// The correction that actually went out for the last step (pixels, the amount by which a positive offset is
    /// reduced): after the max-duration clamp, the Dec guide mode and backlash compensation, 0 when no pulse was sent.
    /// Model-based algorithms use it as the known input of their model.
    /// </summary>
    void CorrectionApplied(double amount)
    {
    }

    /// <summary>Correction to apply when no measurement is available (dark guiding). 0 by default.</summary>
    double DeduceResult() => 0.0;

    void Reset();

    void GuidingStarted() => Reset();

    void GuidingStopped() => Reset();

    void GuidingPaused()
    {
    }

    void GuidingResumed()
    {
    }

    void GuidingDithered(double amount)
    {
    }

    void GuidingDitherSettleDone(bool success)
    {
    }

    void DirectMoveApplied(double amount)
    {
    }

    IReadOnlyList<string> ParamNames { get; }

    bool TryGetParam(string name, out double value);

    bool TrySetParam(string name, double value);
}
