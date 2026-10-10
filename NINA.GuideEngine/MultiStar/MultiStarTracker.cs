// SPDX-License-Identifier: MPL-2.0 AND BSD-3-Clause
// Copyright (c) 2006-2010 Craig Stark.
// Copyright (c) 2012 Bret McKee
// Copyright (c) 2020 Bruce Waddington
// Ported from PHD2 src/guider_multistar.cpp (GuiderMultiStar: AutoSelect, UpdateCurrentPosition,
// RefineOffset, SetLockPosition, GetBoundingBox, IsValid*Position) (a6c02722)

using NINA.GuideEngine.Core;
using NINA.GuideEngine.Stars;

namespace NINA.GuideEngine.MultiStar;

/// <summary>
/// Host-agnostic port of PHD2's multi-star tracking (GuiderMultiStar): tracks the primary star with
/// Star.Find at its previous position, applies the mass-change check and the optional jump filter,
/// and refines the offset with SNR-weighted secondary star displacements (5-frame stabilisation,
/// 5σ enter / 2σ exit, per-star 2.5σ rejection, hot-pixel eviction, reference re-snap after lock
/// moves). Extension: primary-dropout fallback that estimates the primary position from agreeing
/// secondaries. Not thread-safe; call from the guider loop only.
/// </summary>
public sealed class MultiStarTracker
{
    private const int MinSearchRegion = 7;
    private const int MaxSearchRegion = 50;

    private readonly Star primary = new();
    private readonly Star scratch = new();
    private readonly List<TrackedStar> guideStars = new();
    private readonly List<TrackedStarInfo> evicted = new();
    private readonly RunningStats primaryDistStats = new();
    private readonly MassChecker massChecker = new();
    private readonly DistanceChecker distanceChecker = new();
    private MultiStarOptions options;
    private bool stabilizing;
    private bool lockPositionMoved;
    private int starsUsed;
    private GuidePoint lastLock = GuidePoint.Invalid;
    private bool wasPaused;
    private int frameWidth;
    private int frameHeight;

    public MultiStarTracker(StarFinderOptions? finderOptions = null, MultiStarOptions? options = null)
    {
        FinderOptions = finderOptions ?? new StarFinderOptions();
        this.options = options ?? new MultiStarOptions();
        primary.Position = new GuidePoint(0, 0, false);
    }

    /// <summary>Star detection options (search region, HFD limits, saturation, find mode...).</summary>
    public StarFinderOptions FinderOptions { get; set; }

    /// <summary>Multi-star options. Enabling multi-star mode clears the primary distance statistics; disabling it ends stabilisation (PHD2 SetMultiStarMode).</summary>
    public MultiStarOptions Options
    {
        get => options;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value.MultiStarEnabled && !options.MultiStarEnabled)
                primaryDistStats.Clear();
            if (!value.MultiStarEnabled)
                stabilizing = false;
            options = value;
        }
    }

    /// <summary>Optional camera→mount transform (from calibration) used for the RA distance average.</summary>
    public Func<GuidePoint, GuidePoint>? CameraToMount { get; set; }

    /// <summary>Search region clamped to PHD2's 7..50 px range.</summary>
    public int SearchRegion => Math.Clamp(FinderOptions.SearchRegion, MinSearchRegion, MaxSearchRegion);

    /// <summary>The primary guide star (PHD2 m_primaryStar).</summary>
    public Star PrimaryStar => primary;

    /// <summary>Primary (index 0, as selected) followed by the secondary stars.</summary>
    public IReadOnlyList<GuideStar> GuideStars => guideStars;

    /// <summary>True while the primary is found (PHD2 IsLocked).</summary>
    public bool IsLocked => primary.WasFound();

    public bool IsStabilizing => stabilizing;

    /// <summary>Stars used in the last refined offset (PHD2 m_starsUsed).</summary>
    public int StarsUsed => starsUsed;

    /// <summary>Distance averages (PHD2 CurrentError / CurrentErrorSmoothed); usable for settling.</summary>
    public DistanceAverager Distances { get; } = new();

    #region selection

    /// <summary>
    /// Runs AutoFind and selects the primary and secondary stars (PHD2 GuiderMultiStar::AutoSelect). On
    /// success the caller should use <see cref="AutoSelectResult.LockPosition"/> as lock position.
    /// The tracker state is left unchanged on failure.
    /// </summary>
    /// <param name="frame">Full (preprocessed) frame.</param>
    /// <param name="roi">Region of interest, empty for full frame.</param>
    /// <param name="edgeAllowance">Extra edge margin (calibration distance when not calibrated).</param>
    public AutoSelectResult AutoSelect(GuideFrame frame, IntRect roi = default, int edgeAllowance = 0)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var diag = new AutoFindDiagnostics();
        int listSize = options.UseSubframes || !options.MultiStarEnabled ? 1 : options.MaxListSize;
        var list = GuideStar.AutoFind(frame, edgeAllowance, SearchRegion, roi, listSize, FinderOptions, diag);
        // Deviation from PHD2: a failed AutoFind leaves the current guide star list untouched (PHD2's
        // AutoFind may already have cleared m_guideStars).
        if (list.Count == 0)
            return new AutoSelectResult(false, default, list, diag);
        return InitializeCore(frame, list, diag.PrimaryPeak, diag);
    }

    /// <summary>
    /// (Re)initialises from AutoFind results computed elsewhere (list primary first). The primary is
    /// re-measured at <paramref name="primaryHint"/> (default: the first star's position).
    /// </summary>
    public AutoSelectResult Initialize(GuideFrame frame, IReadOnlyList<GuideStar> stars, GuidePoint? primaryHint = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(stars);
        if (stars.Count == 0)
            return new AutoSelectResult(false, default, stars, null);
        return InitializeCore(frame, stars, primaryHint ?? stars[0].Position, null);
    }

    private AutoSelectResult InitializeCore(GuideFrame frame, IReadOnlyList<GuideStar> list, GuidePoint hint, AutoFindDiagnostics? diag)
    {
        frameWidth = frame.Width;
        frameHeight = frame.Height;
        var candidate = new Star();
        if (!candidate.Find(frame, SearchRegion, (int)hint.X, (int)hint.Y, StarFindMode.Centroid, FinderOptions.MinHfd, FinderOptions.MaxHfd,
                FinderOptions.SaturationAdu))
            return new AutoSelectResult(false, StarSnapshot.Of(candidate), list, diag);

        massChecker.Reset();
        CopyStar(candidate, primary);
        guideStars.Clear();
        foreach (var gs in list)
            guideStars.Add(new TrackedStar(gs, primary.Position));
        primaryDistStats.Clear();
        NotifyLockPositionSet();
        lastLock = primary.Position;
        return new AutoSelectResult(true, StarSnapshot.Of(primary), guideStars.ToList(), diag);
    }

    /// <summary>
    /// Manually selects the star near <paramref name="position"/> (PHD2 SetCurrentPosition + OnLClick):
    /// single-star usage, secondary stars are cleared. Returns false when no star was found.
    /// </summary>
    public bool SelectStarAt(GuideFrame frame, GuidePoint position)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (!position.IsValid || position.X <= 0 || position.X >= frame.Width || position.Y <= 0 || position.Y >= frame.Height)
            return false;
        frameWidth = frame.Width;
        frameHeight = frame.Height;
        massChecker.Reset();
        bool found = primary.Find(frame, SearchRegion, (int)position.X, (int)position.Y, FinderOptions.FindMode, FinderOptions.MinHfd,
            FinderOptions.MaxHfd, FinderOptions.SaturationAdu);
        if (!primary.IsValid)
            return false;
        ClearSecondaryStars();
        if (guideStars.Count == 0)
            guideStars.Add(new TrackedStar(new GuideStar(primary), primary.Position));
        NotifyLockPositionSet();
        lastLock = primary.Position;
        return found;
    }

    /// <summary>
    /// Manually selects the star nearest <paramref name="position"/> (within the search region) as the primary.
    /// Deviation from PHD2: a click selection there guides on the clicked star alone (<see cref="SelectStarAt"/>);
    /// here, in multi-star mode, the secondary stars are found with AutoFind around it, as after an auto-selection.
    /// The tracker state is left unchanged on failure.
    /// </summary>
    public StarSelectionResult SelectStar(GuideFrame frame, GuidePoint position)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (!position.IsValid || position.X < 0 || position.X >= frame.Width || position.Y < 0 || position.Y >= frame.Height)
            return StarSelectionResult.Failed(StarSelectionError.NoStar);
        var candidate = new Star();
        if (!candidate.Find(frame, SearchRegion, (int)position.X, (int)position.Y, StarFindMode.Centroid, FinderOptions.MinHfd,
                FinderOptions.MaxHfd, FinderOptions.SaturationAdu))
            return StarSelectionResult.Failed(StarSelectionError.NoStar, StarSnapshot.Of(candidate));
        frameWidth = frame.Width;
        frameHeight = frame.Height;
        if (!IsValidLockPosition(candidate.Position))
            return StarSelectionResult.Failed(StarSelectionError.NearEdge, StarSnapshot.Of(candidate));

        var list = new List<GuideStar> { new(candidate) };
        list.AddRange(FindSecondaries(frame, candidate.Position));
        var r = InitializeCore(frame, list, candidate.Position, null);
        return r.Success
            ? new StarSelectionResult(StarSelectionError.None, r.Primary, list.Count - 1)
            : StarSelectionResult.Failed(StarSelectionError.NoStar, r.Primary);
    }

    /// <summary>
    /// Extension (not in PHD2): replaces the secondary stars with stars found by AutoFind around the current primary,
    /// for when the old ones keep being lost (the primary switched to another star, or the field moved). A lost
    /// secondary is otherwise only searched at its original offset from the primary and never recovers. The list is
    /// kept when no star, or fewer than half as many stars as before, are found (e.g. under clouds). After a
    /// replacement the tracker stabilises and re-snaps the new reference points near the lock position, as after a
    /// lock position change.
    /// </summary>
    /// <param name="frame">Full frame the primary was just measured on.</param>
    /// <param name="force">
    /// False: first look for the current secondaries at their offsets from the primary, and keep them when at least
    /// half are there. True: the caller already knows they are lost.
    /// </param>
    public SecondaryRefreshResult RefreshSecondaryStars(GuideFrame frame, bool force)
    {
        ArgumentNullException.ThrowIfNull(frame);
        int before = Math.Max(0, guideStars.Count - 1);
        if (!primary.WasFound())
            return new SecondaryRefreshResult(before, 0, false);
        if (!force && before > 0 && CountSecondariesAtOffsets(frame) * 2 >= before)
            return new SecondaryRefreshResult(before, 0, false);
        var found = FindSecondaries(frame, primary.Position);
        if (found.Count == 0 || found.Count * 2 < before)
            return new SecondaryRefreshResult(before, found.Count, false);

        guideStars.Clear();
        guideStars.Add(new TrackedStar(new GuideStar(primary), primary.Position));
        foreach (var gs in found)
            guideStars.Add(new TrackedStar(gs, primary.Position));
        NotifyLockPositionSet();
        return new SecondaryRefreshResult(before, found.Count, true);
    }

    /// <summary>Number of secondary stars found at their offsets from the current primary (the tracked stars are not changed).</summary>
    private int CountSecondariesAtOffsets(GuideFrame frame)
    {
        var fo = FinderOptions;
        var probe = new Star();
        int present = 0;
        for (int i = 1; i < guideStars.Count; i++)
        {
            var expected = primary.Position + guideStars[i].OffsetFromPrimary;
            if (IsValidSecondaryStarPosition(expected) &&
                probe.Find(frame, SearchRegion, (int)expected.X, (int)expected.Y, fo.FindMode, fo.MinHfd, fo.MaxHfd, fo.SaturationAdu))
                present++;
        }

        return present;
    }

    /// <summary>AutoFind's stars except the one at (or crowding) <paramref name="primaryPos"/>, with offsets from it; empty in single-star mode.</summary>
    private List<GuideStar> FindSecondaries(GuideFrame frame, GuidePoint primaryPos)
    {
        var list = new List<GuideStar>();
        if (!options.MultiStarEnabled || options.UseSubframes || !frame.Subframe.IsEmpty)
            return list;
        foreach (var gs in GuideStar.AutoFind(frame, 0, SearchRegion, IntRect.Empty, options.MaxListSize, FinderOptions))
        {
            if (list.Count >= options.MaxListSize - 1)
                break;
            if ((gs.Position - primaryPos).Distance() <= SearchRegion)
                continue;
            gs.OffsetFromPrimary = gs.ReferencePoint - primaryPos;
            list.Add(gs);
        }

        return list;
    }

    /// <summary>Removes all secondary stars (PHD2 ClearSecondaryStars).</summary>
    public void ClearSecondaryStars()
    {
        if (guideStars.Count > 1)
            guideStars.RemoveRange(1, guideStars.Count - 1);
    }

    /// <summary>PHD2 InvalidateCurrentPosition: the primary becomes invalid; with a full reset no star is selected any more.</summary>
    public void InvalidateCurrentPosition(bool fullReset = false)
    {
        primary.Invalidate();
        if (fullReset)
            primary.Position = new GuidePoint(0, 0, false);
    }

    /// <summary>
    /// PHD2 GuiderMultiStar::SetLockPosition side effects: in multi-star mode, stabilise and re-snap the
    /// secondary references once stable. Called automatically when the lock position passed to
    /// <see cref="ProcessFrame"/> changes; call explicitly when the guider re-sets an unchanged lock position.
    /// </summary>
    public void NotifyLockPositionSet()
    {
        if (options.MultiStarEnabled)
        {
            lockPositionMoved = true;
            stabilizing = true;
        }
    }

    /// <summary>Adds a dither to the distance averages (PHD2 MoveLockPosition).</summary>
    public void AddDitherDistance(double cameraDistance, double raDistance) => Distances.AddDither(cameraDistance, raDistance);

    #endregion

    #region per frame

    /// <summary>
    /// Processes one preprocessed frame (PHD2 UpdateCurrentPosition + RefineOffset + dropout fallback).
    /// </summary>
    /// <param name="frame">Preprocessed frame.</param>
    /// <param name="lockPosition">Current lock position (invalid when none).</param>
    /// <param name="state">Guider state flags.</param>
    /// <param name="timestamp">Frame time (drives the mass checker window and jump filter; no wall clock).</param>
    public MultiStarFrameResult ProcessFrame(GuideFrame frame, GuidePoint lockPosition, TrackerState state, DateTimeOffset timestamp)
    {
        ArgumentNullException.ThrowIfNull(frame);
        long now = timestamp.ToUnixTimeMilliseconds();
        frameWidth = frame.Width;
        frameHeight = frame.Height;
        evicted.Clear();
        foreach (var gs in guideStars)
            gs.ResetFrameStatus();

        // Deviation from PHD2: PHD2 resets the distance averages only when leaving a *full* pause; the
        // tracker only knows "paused" and resets on leaving any pause.
        if (wasPaused && !state.IsPaused)
            Distances.NeedReset = true;
        wasPaused = state.IsPaused;

        if (lockPosition.IsValid && (!lastLock.IsValid || lockPosition.X != lastLock.X || lockPosition.Y != lastLock.Y))
            NotifyLockPositionSet();
        lastLock = lockPosition;

        if (!primary.IsValid && primary.X == 0.0 && primary.Y == 0.0)
        {
            return new MultiStarFrameResult
            {
                Outcome = TrackerOutcome.NoStarSelected,
                Primary = default,
                ErrorCode = StarFindResult.Error,
                CameraOffset = GuidePoint.Invalid,
                Stars = BuildInfos(TrackedStarStatus.PrimaryLost),
                Status = "No star selected",
            };
        }

        int sr = SearchRegion;
        var newStar = scratch;
        CopyStar(primary, newStar);
        bool found = newStar.Find(frame, sr, FinderOptions.FindMode, FinderOptions.MinHfd, FinderOptions.MaxHfd, FinderOptions.SaturationAdu);
        if (!found)
        {
            primary.SetError(newStar.LastFindResult);
            return HandleLost(frame, lockPosition, state, now, newStar.LastFindResult, StarSnapshot.Of(newStar), null);
        }

        if (options.SaturatedPrimaryIsLost && newStar.LastFindResult == StarFindResult.Saturated)
        {
            // Extension: configured to treat a saturated primary as lost.
            primary.SetError(StarFindResult.Saturated);
            return HandleLost(frame, lockPosition, state, now, StarFindResult.Saturated, StarSnapshot.Of(newStar), null);
        }

        // check to see if it seems like the star we just found was the same as the original star by
        // comparing the mass
        MassLimits? massLimits = null;
        if (options.MassChangeThresholdEnabled)
        {
            massChecker.SetExposure(frame.ExposureMs, options.AutoExposure);
            if (massChecker.CheckMass(newStar.Mass, options.MassChangeThreshold, out var limits))
            {
                primary.SetError(StarFindResult.MassChange);
                massChecker.AppendData(newStar.Mass, now);
                return HandleLost(frame, lockPosition, state, now, StarFindResult.MassChange,
                    StarSnapshot.Of(newStar) with { Result = StarFindResult.MassChange }, limits);
            }

            if (massChecker.Count >= 5)
                massLimits = limits;
        }

        double distance;
        bool raOnly = state.RaOnly;
        if (lockPosition.IsValid)
            distance = raOnly ? Math.Abs(newStar.X - lockPosition.X) : newStar.Position.Distance(lockPosition);
        else
            distance = 0.0;

        double tolerance = options.TolerateJumpsEnabled ? options.TolerateJumpsThreshold : 9e99;
        if (!distanceChecker.CheckDistance(distance, raOnly, tolerance, state.IsGuiding, state.IsPaused, state.IsSettling, Distances, now))
        {
            primary.SetError(StarFindResult.Error);
            return new MultiStarFrameResult
            {
                Outcome = TrackerOutcome.JumpRejected,
                Primary = StarSnapshot.Of(newStar) with { Result = StarFindResult.Error },
                ErrorCode = StarFindResult.Error,
                CameraOffset = GuidePoint.Invalid,
                MassLimits = massLimits,
                Stars = BuildInfos(TrackedStarStatus.PrimaryLost),
                Status = "Recovering",
                Stabilizing = stabilizing,
            };
        }

        // update the star position, mass, etc.
        CopyStar(newStar, primary);
        massChecker.AppendData(newStar.Mass, now);

        var offset = GuidePoint.Invalid;
        var primaryOnly = GuidePoint.Invalid;
        bool refined = false;
        if (lockPosition.IsValid)
        {
            offset = primary.Position - lockPosition;
            primaryOnly = offset;
            // Deviation from PHD2: secondaries are not measured on subframed frames (PHD2 relies on the list
            // having a single star in subframe mode).
            if (options.MultiStarEnabled && guideStars.Count > 1 && frame.Subframe.IsEmpty)
            {
                if (RefineOffset(frame, ref offset, state))
                {
                    refined = true;
                    distance = Math.Sqrt(offset.X * offset.X + offset.Y * offset.Y); // Distance is reported to clients
                }
            }
            else
            {
                starsUsed = 1;
            }

            Distances.Update(distance, RaDistance(offset), state.IsGuiding, now);
        }

        string status = $"m={primary.Mass:F0} SNR={primary.Snr:F1}" + (primary.LastFindResult == StarFindResult.Saturated ? " Saturated" : string.Empty);
        return new MultiStarFrameResult
        {
            Outcome = TrackerOutcome.Found,
            Primary = StarSnapshot.Of(primary),
            ErrorCode = primary.LastFindResult,
            CameraOffset = offset,
            PrimaryOnlyOffset = primaryOnly,
            Refined = refined,
            StarsUsed = starsUsed,
            Stabilizing = stabilizing,
            Distance = distance,
            MassLimits = massLimits,
            Stars = BuildInfos(TrackedStarStatus.Primary),
            Status = status,
        };
    }

    private double RaDistance(GuidePoint cameraOffset)
    {
        if (CameraToMount is null || !cameraOffset.IsValid) return 0.0;
        var m = CameraToMount(cameraOffset);
        return m.IsValid ? Math.Abs(m.X) : 0.0;
    }

    /// <summary>Use secondary stars to refine the offset (PHD2 RefineOffset). Returns true when the offset was replaced.</summary>
    private bool RefineOffset(GuideFrame img, ref GuidePoint offset, TrackerState state)
    {
        double primarySigma = 0;
        bool averaged = false;
        var origOffset = offset;
        starsUsed = 1;
        bool refined = false;
        int sr = SearchRegion;
        var fo = FinderOptions;

        // Primary star is in position 0 of the list
        if (state.IsGuiding && guideStars.Count > 1 && state.GuidingEnabled && !state.IsSettling)
        {
            double sumWeights = 1;
            double sumX = origOffset.X;
            double sumY = origOffset.Y;
            double primaryDistance = Hypot(sumX, sumY);

            primaryDistStats.AddValue(primaryDistance);

            if (primaryDistStats.Count > 5)
            {
                primarySigma = primaryDistStats.Sigma;
                if (!stabilizing && primaryDistance > options.StabilitySigmaX * primarySigma)
                {
                    stabilizing = true; // large primary error, entering stabilization period
                }
                else if (stabilizing)
                {
                    if (primaryDistance <= 2 * primarySigma)
                    {
                        stabilizing = false; // exiting stabilization period
                        if (lockPositionMoved)
                        {
                            lockPositionMoved = false;
                            // updating star positions after lock position change
                            for (int i = 1; i < guideStars.Count; i++)
                            {
                                var gs = guideStars[i];
                                var expectedLoc = primary.Position + gs.OffsetFromPrimary;
                                bool found = IsValidSecondaryStarPosition(expectedLoc)
                                    ? gs.Find(img, sr, (int)expectedLoc.X, (int)expectedLoc.Y, fo.FindMode, fo.MinHfd, fo.MaxHfd, fo.SaturationAdu)
                                    : gs.Find(img, sr, (int)gs.X, (int)gs.Y, fo.FindMode, fo.MinHfd, fo.MaxHfd, fo.SaturationAdu);
                                if (found)
                                {
                                    gs.ReferencePoint = gs.Position;
                                    gs.PrimaryAtReference = primary.Position;
                                    gs.WasLost = false;
                                    gs.FrameStatus = TrackedStarStatus.Resnapped;
                                }
                                else
                                {
                                    // lost star will continue to use the offsetFromPrimary location for possible recovery
                                    gs.WasLost = true;
                                    gs.FrameStatus = TrackedStarStatus.Lost;
                                }
                            }

                            return false; // All the secondary stars reference points reflect current positions
                        }
                    }
                }
            }
            else
            {
                stabilizing = true; // get some data for primary star movement
            }

            if (!stabilizing && guideStars.Count > 1 && (sumX != 0 || sumY != 0))
            {
                int maxStars = fo.MaxStars;
                int idx = 1;
                while (idx < guideStars.Count)
                {
                    if (starsUsed >= maxStars || guideStars.Count == 1)
                        break;
                    var gs = guideStars[idx];
                    bool found;
                    if (gs.WasLost)
                    {
                        // Look for it based on its original offset from the primary star
                        var expectedLoc = primary.Position + gs.OffsetFromPrimary;
                        found = gs.Find(img, sr, (int)expectedLoc.X, (int)expectedLoc.Y, fo.FindMode, fo.MinHfd, fo.MaxHfd, fo.SaturationAdu);
                    }
                    else
                    {
                        // Look for it where we last found it
                        found = gs.Find(img, sr, (int)gs.X, (int)gs.Y, fo.FindMode, fo.MinHfd, fo.MaxHfd, fo.SaturationAdu);
                    }

                    if (found)
                    {
                        double dX = gs.X - gs.ReferencePoint.X;
                        double dY = gs.Y - gs.ReferencePoint.Y;
                        gs.Displacement = new GuidePoint(dX, dY);

                        gs.WasLost = false;
                        starsUsed++;

                        if (dX != 0.0 || dY != 0.0)
                        {
                            // Handle zero-counting - suspect results of exactly zero movement
                            if (dX == 0.0 || dY == 0.0)
                                ++gs.ZeroCount;
                            else if (gs.ZeroCount > 0)
                                --gs.ZeroCount;

                            if (gs.ZeroCount == 5)
                            {
                                // Deviation from PHD2: PHD2 leaves its 'erasures' flag set on this path, which makes
                                // it process the following star twice (double weight). We move on normally.
                                Evict(idx);
                                continue;
                            }

                            // Handle suspicious excursions - counted as "misses"
                            double secondaryDistance = Hypot(dX, dY);
                            if (secondaryDistance > 2.5 * primarySigma)
                            {
                                if (++gs.MissCount > 10)
                                {
                                    // Reset the reference point to wherever it is now
                                    gs.ReferencePoint = gs.Position;
                                    gs.PrimaryAtReference = primary.Position;
                                    gs.MissCount = 0;
                                    gs.FrameStatus = TrackedStarStatus.ReferenceReset;
                                }
                                else
                                {
                                    gs.FrameStatus = TrackedStarStatus.Miss;
                                }

                                idx++;
                                continue;
                            }
                            else if (gs.MissCount > 0)
                            {
                                --gs.MissCount;
                            }

                            // At this point we have usable data from the secondary star
                            double wt = gs.Snr / primary.Snr;
                            sumX += wt * dX;
                            sumY += wt * dY;
                            sumWeights += wt;
                            averaged = true;
                            gs.FrameStatus = TrackedStarStatus.Used;
                            gs.FrameWeight = wt;
                        }
                        else
                        {
                            // exactly zero on both axes, probably a hot pixel, drop it
                            Evict(idx);
                            continue;
                        }
                    }
                    else
                    {
                        // star not found in its search region
                        gs.FrameStatus = TrackedStarStatus.Lost;
                        gs.WasLost = true;
                    }

                    idx++;
                }

                if (averaged)
                {
                    sumX /= sumWeights;
                    sumY /= sumWeights;
                    if (Hypot(sumX, sumY) < primaryDistance) // Apply average only if its smaller than single-star delta
                    {
                        offset = new GuidePoint(sumX, sumY);
                        refined = true;
                    }
                }
            }
        }

        return refined;
    }

    private void Evict(int idx)
    {
        var gs = guideStars[idx];
        gs.FrameStatus = TrackedStarStatus.DroppedZero;
        evicted.Add(gs.ToInfo(idx));
        guideStars.RemoveAt(idx);
    }

    private MultiStarFrameResult HandleLost(GuideFrame frame, GuidePoint lockPosition, TrackerState state, long now, StarFindResult code,
        StarSnapshot measured, MassLimits? limits)
    {
        if (options.PrimaryDropoutFallback && options.MultiStarEnabled && !options.UseSubframes && frame.Subframe.IsEmpty && lockPosition.IsValid &&
            state.IsGuiding && guideStars.Count - 1 >= options.FallbackMinStars && TryEstimatePrimary(frame, out var estimate, out int used))
        {
            primary.Position = estimate; // search hint for the next frame; the error code is kept
            var offset = estimate - lockPosition;
            double distance = state.RaOnly ? Math.Abs(offset.X) : offset.Distance();
            Distances.Update(distance, RaDistance(offset), state.IsGuiding, now);
            starsUsed = used;
            return new MultiStarFrameResult
            {
                Outcome = TrackerOutcome.Estimated,
                Primary = new StarSnapshot(estimate, primary.Mass, primary.Snr, primary.Hfd, primary.PeakValue, code),
                ErrorCode = code,
                CameraOffset = offset,
                StarsUsed = used,
                Stabilizing = stabilizing,
                Distance = distance,
                MassLimits = limits,
                Stars = BuildInfos(TrackedStarStatus.PrimaryLost),
                Status = $"{LostText(code)} - position estimated from {used} stars",
            };
        }

        // PHD2 arms the jump filter (forced 2x tolerance for 5 s) whenever the primary is lost. Deviation
        // (extension): not when the fallback provided a position.
        distanceChecker.Activate(now);
        return new MultiStarFrameResult
        {
            Outcome = TrackerOutcome.Lost,
            Primary = measured,
            ErrorCode = code,
            CameraOffset = GuidePoint.Invalid,
            Stabilizing = stabilizing,
            MassLimits = limits,
            Stars = BuildInfos(TrackedStarStatus.PrimaryLost),
            Status = LostText(code),
        };
    }

    /// <summary>
    /// Extension (own code): estimate the primary from secondaries. Each found secondary i gives
    /// <c>estimate_i = primaryAtReference_i + (position_i − reference_i)</c>; estimates within
    /// max(2.5σ, floor) of the component-wise median agree. With at least FallbackMinStars agreeing
    /// (and at least half of the measured ones) the estimate is the median of the agreeing ones.
    /// </summary>
    private bool TryEstimatePrimary(GuideFrame frame, out GuidePoint estimate, out int used)
    {
        estimate = GuidePoint.Invalid;
        used = 0;
        var fo = FinderOptions;
        int sr = SearchRegion;
        var candidates = new List<(TrackedStar Star, GuidePoint Est)>();
        for (int i = 1; i < guideStars.Count; i++)
        {
            var gs = guideStars[i];
            var hint = gs.WasLost ? primary.Position + gs.OffsetFromPrimary : gs.Position;
            if (!gs.Find(frame, sr, (int)hint.X, (int)hint.Y, fo.FindMode, fo.MinHfd, fo.MaxHfd, fo.SaturationAdu))
            {
                gs.WasLost = true;
                gs.FrameStatus = TrackedStarStatus.Lost;
                continue;
            }

            gs.WasLost = false;
            var d = gs.Position - gs.ReferencePoint;
            gs.Displacement = d;
            if (d.X == 0.0 && d.Y == 0.0)
            {
                gs.FrameStatus = TrackedStarStatus.FallbackRejected; // hot pixel suspect
                continue;
            }

            candidates.Add((gs, gs.PrimaryAtReference + d));
        }

        if (candidates.Count < options.FallbackMinStars)
        {
            foreach (var c in candidates) c.Star.FrameStatus = TrackedStarStatus.FallbackRejected;
            return false;
        }

        var med = MedianPoint(candidates.Select(c => c.Est).ToList());
        double sigma = primaryDistStats.Count > 5 ? primaryDistStats.Sigma : 0.0;
        double gate = Math.Max(2.5 * sigma, options.FallbackAgreementFloorPx);
        var agree = candidates.Where(c => c.Est.Distance(med) <= gate).ToList();
        if (agree.Count < options.FallbackMinStars || agree.Count * 2 < candidates.Count)
        {
            foreach (var c in candidates) c.Star.FrameStatus = TrackedStarStatus.FallbackRejected;
            return false;
        }

        foreach (var c in candidates)
            c.Star.FrameStatus = agree.Contains(c) ? TrackedStarStatus.FallbackUsed : TrackedStarStatus.FallbackRejected;
        estimate = MedianPoint(agree.Select(c => c.Est).ToList());
        used = agree.Count;
        return true;
    }

    private static GuidePoint MedianPoint(List<GuidePoint> pts)
    {
        var xs = pts.Select(p => p.X).OrderBy(v => v).ToArray();
        var ys = pts.Select(p => p.Y).OrderBy(v => v).ToArray();
        int n = xs.Length;
        double mx = n % 2 == 1 ? xs[n / 2] : 0.5 * (xs[n / 2 - 1] + xs[n / 2]);
        double my = n % 2 == 1 ? ys[n / 2] : 0.5 * (ys[n / 2 - 1] + ys[n / 2]);
        return new GuidePoint(mx, my);
    }

    #endregion

    #region geometry helpers

    /// <summary>PHD2 IsValidLockPosition: the search region around the point must fit in the frame.</summary>
    public bool IsValidLockPosition(GuidePoint pt)
    {
        int sr = SearchRegion;
        return pt.X >= 1 + sr && pt.X + 1 + sr < frameWidth && pt.Y >= 1 + sr && pt.Y + 1 + sr < frameHeight;
    }

    /// <summary>PHD2 IsValidSecondaryStarPosition (relaxed: 5 px from the edges).</summary>
    public bool IsValidSecondaryStarPosition(GuidePoint pt)
        => pt.X >= 5 && pt.X + 5 < frameWidth && pt.Y >= 5 && pt.Y + 5 < frameHeight;

    /// <summary>
    /// Subframe to request for the next exposure in subframe mode (PHD2 GetBoundingBox); empty = full frame.
    /// </summary>
    public IntRect GetBoundingBox(BoundingBoxState state, GuidePoint lockPosition, int sensorWidth, int sensorHeight, bool forceFullFrame = false)
    {
        bool subframe;
        GuidePoint pos = GuidePoint.Invalid;
        int sr = SearchRegion;
        switch (state)
        {
            case BoundingBoxState.Selected:
            case BoundingBoxState.Calibrating:
                subframe = primary.WasFound();
                pos = primary.Position;
                break;
            case BoundingBoxState.Guiding:
            {
                subframe = primary.WasFound();
                // As long as the star is close to the lock position, keep the subframe at the lock
                // position. Otherwise, follow the star.
                double dist = lockPosition.IsValid ? primary.Position.Distance(lockPosition) : double.MaxValue;
                pos = (int)Math.Min(dist, int.MaxValue) > sr / 3 ? primary.Position : lockPosition;
                break;
            }

            default:
                subframe = false;
                break;
        }

        if (forceFullFrame || !subframe)
            return IntRect.Empty;
        var box = new IntRect((int)Math.Floor(pos.X + 0.5) - sr, (int)Math.Floor(pos.Y + 0.5) - sr, 2 * sr + 1, 2 * sr + 1);
        return box.Intersect(new IntRect(0, 0, sensorWidth, sensorHeight));
    }

    #endregion

    private static double Hypot(double x, double y) => Math.Sqrt(x * x + y * y);

    private static void CopyStar(Star from, Star to)
    {
        to.Position = from.Position;
        to.Mass = from.Mass;
        to.Snr = from.Snr;
        to.Hfd = from.Hfd;
        to.PeakValue = from.PeakValue;
        to.LastFindResult = from.LastFindResult;
    }

    private static string LostText(StarFindResult r) => r switch
    {
        StarFindResult.LowSnr => "Star lost - low SNR",
        StarFindResult.LowMass => "Star lost - low mass",
        StarFindResult.LowHfd => "Star lost - low HFD",
        StarFindResult.TooNearEdge => "Star too near edge",
        StarFindResult.MassChange => "Star lost - mass changed",
        StarFindResult.Saturated => "Star lost - saturated",
        _ => "No star found",
    };

    private List<TrackedStarInfo> BuildInfos(TrackedStarStatus primaryStatus)
    {
        var list = new List<TrackedStarInfo>(guideStars.Count + evicted.Count + 1)
        {
            new(0, StarSnapshot.Of(primary), guideStars.Count > 0 ? guideStars[0].ReferencePoint : primary.Position, primaryStatus,
                primaryStatus == TrackedStarStatus.Primary ? 1.0 : 0.0, GuidePoint.Invalid, 0, 0),
        };
        for (int i = 1; i < guideStars.Count; i++)
            list.Add(guideStars[i].ToInfo(i));
        list.AddRange(evicted);
        return list;
    }

    /// <summary>Guide star with tracker bookkeeping.</summary>
    private sealed class TrackedStar : GuideStar
    {
        public TrackedStar(GuideStar gs, GuidePoint primaryAtReference)
            : base(gs)
        {
            ReferencePoint = gs.ReferencePoint;
            OffsetFromPrimary = gs.OffsetFromPrimary;
            MissCount = gs.MissCount;
            ZeroCount = gs.ZeroCount;
            WasLost = gs.WasLost;
            PrimaryAtReference = primaryAtReference;
        }

        /// <summary>Primary position when <see cref="GuideStar.ReferencePoint"/> was set (dropout fallback).</summary>
        public GuidePoint PrimaryAtReference { get; set; }

        public TrackedStarStatus FrameStatus { get; set; }

        public double FrameWeight { get; set; }

        public GuidePoint Displacement { get; set; }

        public void ResetFrameStatus()
        {
            FrameStatus = TrackedStarStatus.NotMeasured;
            FrameWeight = 0;
            Displacement = GuidePoint.Invalid;
        }

        public TrackedStarInfo ToInfo(int index)
            => new(index, StarSnapshot.Of(this), ReferencePoint, FrameStatus, FrameWeight, Displacement, MissCount, ZeroCount);
    }
}
