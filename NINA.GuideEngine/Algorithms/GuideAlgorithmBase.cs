// SPDX-License-Identifier: MPL-2.0 AND BSD-3-Clause
// Copyright (c) 2013-2016 Andy Galasso
// Copyright (c) 2006-2010 Craig Stark
// Ported from PHD2 src/guide_algorithm.cpp (a6c02722)

namespace NINA.GuideEngine.Algorithms;

/// <summary>Guide algorithm kinds, numbered like PHD2's <c>GUIDE_ALGORITHM</c> enum.</summary>
public enum GuideAlgorithmKind
{
    None = -1,
    Identity = 0,
    Hysteresis = 1,
    Lowpass = 2,
    Lowpass2 = 3,
    ResistSwitch = 4,
    GaussianProcess = 5,
    ZFilter = 6,

    /// <summary>Model-based (Kalman) algorithm of this engine (<see cref="PredictiveAlgorithm"/>); not a PHD2 kind.</summary>
    Predictive = 100,
}

/// <summary>
/// Base class for the PHD2 algorithm ports. Lifecycle notifications default to PHD2's
/// <c>GuideAlgorithm</c> base behaviour (which differs from the interface defaults):
/// started = no-op, stopped/resumed/dithered/enabled = reset.
/// </summary>
public abstract class GuideAlgorithmBase : IGuideAlgorithm
{
    /// <summary>PHD2 class name (<c>GetGuideAlgorithmClassName</c>), e.g. "Hysteresis".</summary>
    public abstract string Name { get; }

    public abstract GuideAlgorithmKind Kind { get; }

    /// <summary>
    /// Minimum move in pixels. Algorithms without a min-move return -1 (PHD2 convention).
    /// Setting an invalid value applies PHD2's fallback (usually the default).
    /// </summary>
    public virtual double MinMove
    {
        get => -1.0;
        set { }
    }

    /// <summary>True when the algorithm has a min-move parameter (<see cref="MinMove"/> &gt;= 0).</summary>
    public bool HasMinMove => MinMove >= 0.0;

    public abstract double Result(double input);

    public virtual double DeduceResult() => 0.0;

    public abstract void Reset();

    public virtual void GuidingStarted()
    {
    }

    public virtual void GuidingStopped() => Reset();

    public virtual void GuidingPaused()
    {
    }

    public virtual void GuidingResumed() => Reset();

    public virtual void GuidingDithered(double amount) => Reset();

    public virtual void GuidingDitherSettleDone(bool success)
    {
    }

    public virtual void DirectMoveApplied(double amount)
    {
    }

    /// <summary>Guiding output re-enabled: discard history accumulated while disabled.</summary>
    public virtual void GuidingEnabled() => Reset();

    /// <summary>Guiding output disabled: history keeps accumulating by default.</summary>
    public virtual void GuidingDisabled()
    {
    }

    public virtual IReadOnlyList<string> ParamNames => [];

    public virtual bool TryGetParam(string name, out double value)
    {
        value = 0.0;
        return false;
    }

    /// <summary>
    /// Sets a parameter by its PHD2 name. Returns false for unknown names or out-of-range values; like
    /// PHD2, an out-of-range value still applies the algorithm's fallback value.
    /// </summary>
    public virtual bool TrySetParam(string name, double value) => false;

    /// <summary>Loggable settings summary (PHD2 <c>GetSettingsSummary</c>).</summary>
    public virtual string SettingsSummary => string.Empty;

    /// <summary>
    /// Restores all parameters to their defaults and, for algorithms with a min-move, applies
    /// <paramref name="smartMinMove"/> (PHD2 <c>ResetParams</c>).
    /// </summary>
    public void ResetParams(double smartMinMove)
    {
        RestoreDefaults();
        if (HasMinMove)
            MinMove = smartMinMove;
    }

    /// <summary>Restores parameter defaults (the equivalent of deleting the PHD2 profile group).</summary>
    protected abstract void RestoreDefaults();

    public override string ToString() => Name;
}
