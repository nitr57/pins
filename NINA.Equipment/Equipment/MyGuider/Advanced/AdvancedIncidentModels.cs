#region "copyright"

/*
    Copyright © 2025-2026 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

#nullable enable annotations

using NINA.Equipment.Interfaces;
using System;
using System.Collections.Generic;

namespace NINA.Equipment.Equipment.MyGuider.Advanced {

    /// <summary>Values of <see cref="AdvancedIncidentSummary.Kind"/>, <see cref="AdvancedIncidentSummary.Kinds"/> and <see cref="AdvancedIncidentTrigger.Kind"/>.</summary>
    public static class AdvancedIncidentKinds {
        public const string StarLost = "StarLost";
        public const string Runaway = "Runaway";
        public const string MountNotResponding = "MountNotResponding";
        public const string SettleTimeout = "SettleTimeout";
        public const string CameraFailure = "CameraFailure";
        public const string MountPaused = "MountPaused";
        public const string CalibrationFailed = "CalibrationFailed";
        public const string PulseLimited = "PulseLimited";
        public const string PulseOutputFailed = "PulseOutputFailed";
        public const string DecFlipCorrected = "DecFlipCorrected";
        public const string Spike = "Spike";

        /// <summary>Marked by the user (<see cref="IGuideIncidentRecorder.MarkIncident"/>).</summary>
        public const string Manual = "Manual";
    }

    /// <summary>The recorded incidents and the flight recorder's storage budget.</summary>
    public class AdvancedIncidentList {
        /// <summary>Newest first; plain summaries, never the full <see cref="AdvancedIncident"/>.</summary>
        public List<AdvancedIncidentSummary> Incidents { get; set; } = new List<AdvancedIncidentSummary>();

        /// <summary>False when the recorder is turned off in the settings.</summary>
        public bool Enabled { get; set; }

        /// <summary>Id of the incident being recorded right now, null when none.</summary>
        public string? RecordingId { get; set; }

        /// <summary>Storage used and allowed, bytes.</summary>
        public long UsedBytes { get; set; }

        public long BudgetBytes { get; set; }
        public int MaxIncidents { get; set; }

        /// <summary>Incidents recorded with the simulator have their own, smaller budget, bytes.</summary>
        public long SimulatorUsedBytes { get; set; }

        public long SimulatorBudgetBytes { get; set; }
    }

    /// <summary>An incident without its frames: what happened, when, how it ended and its likely cause.</summary>
    public class AdvancedIncidentSummary {
        public string Id { get; set; }

        /// <summary>First and last recorded moment, UTC.</summary>
        public DateTime Start { get; set; }

        public DateTime End { get; set; }

        /// <summary>
        /// Kind of the first trigger (<see cref="AdvancedIncidentKinds"/>): StarLost, Runaway, MountNotResponding, SettleTimeout,
        /// CameraFailure, MountPaused, CalibrationFailed, PulseLimited, PulseOutputFailed, DecFlipCorrected, Spike or Manual.
        /// </summary>
        public string Kind { get; set; }

        /// <summary>All trigger kinds of the incident, in order of appearance.</summary>
        public List<string> Kinds { get; set; } = new List<string>();

        /// <summary>How often the first kind happened (a repeat within 10 minutes joins the incident, which is then ongoing).</summary>
        public int Occurrences { get; set; }

        public bool Ongoing { get; set; }

        /// <summary>recording (still open), recovered, stopped (guiding or calibration ended), cap (5-minute limit) or manual.</summary>
        public string EndReason { get; set; }

        public bool Kept { get; set; }

        /// <summary>Size on disk, bytes.</summary>
        public long SizeBytes { get; set; }

        public int FrameCount { get; set; }

        /// <summary>Why the frame images are missing: null (they are there), diskSpace or budget.</summary>
        public string? FramesOmitted { get; set; }

        /// <summary>Note of a manual mark, null when none.</summary>
        public string? Note { get; set; }

        /// <summary>Likely cause (see <see cref="AdvancedIncidentDiagnosis.Cause"/>), null while recording.</summary>
        public string? Cause { get; set; }

        public AdvancedIncidentTags Tags { get; set; }
    }

    /// <summary>Where an incident was recorded: profile, equipment and image scales.</summary>
    public class AdvancedIncidentTags {
        /// <summary>Profile, guide camera and mount; null when unknown.</summary>
        public string? ProfileId { get; set; }

        public string? ProfileName { get; set; }
        public string? GuideCamera { get; set; }
        public string? Mount { get; set; }
        public bool Simulator { get; set; }

        /// <summary>Guide camera scale, arcsec/px.</summary>
        public double PixelScale { get; set; }

        /// <summary>Imaging camera scale, arcsec/px, null when unknown.</summary>
        public double? ImagingScale { get; set; }
    }

    /// <summary>A recorded incident with its triggers, timeline, diagnosis and per-frame telemetry.</summary>
    public class AdvancedIncident : AdvancedIncidentSummary {
        public List<AdvancedIncidentTrigger> Triggers { get; set; } = new List<AdvancedIncidentTrigger>();

        /// <summary>Timeline marks: trigger, recovered, note, gap (frames between repeats that were not kept), end.</summary>
        public List<AdvancedIncidentMarker> Markers { get; set; } = new List<AdvancedIncidentMarker>();

        /// <summary>The likely cause and its evidence; null while recording.</summary>
        public AdvancedIncidentDiagnosis? Diagnosis { get; set; }

        /// <summary>Guide camera sensor size (the coordinate system of positions and crops), px, and the binning of the context images.</summary>
        public int SensorWidth { get; set; }

        public int SensorHeight { get; set; }
        public int ContextBinning { get; set; }

        /// <summary>Star search region half-size in sensor pixels (for the search-region box around the lock position).</summary>
        public int SearchRegionPx { get; set; }

        /// <summary>Per-frame telemetry, oldest first.</summary>
        public List<AdvancedIncidentFrame> Frames { get; set; } = new List<AdvancedIncidentFrame>();
    }

    /// <summary>Something that started or joined an incident.</summary>
    public class AdvancedIncidentTrigger {
        /// <summary>UTC.</summary>
        public DateTime Time { get; set; }

        /// <summary>One of <see cref="AdvancedIncidentKinds"/>.</summary>
        public string Kind { get; set; }

        /// <summary>Alert code and name when an alert triggered it, null for spikes and manual marks.</summary>
        public int? Code { get; set; }

        public string? CodeName { get; set; }
        public string Message { get; set; }

        /// <summary>Details, null when none.</summary>
        public string? Detail { get; set; }

        public long? Frame { get; set; }
    }

    /// <summary>A mark on an incident's timeline.</summary>
    public class AdvancedIncidentMarker {
        /// <summary>UTC.</summary>
        public DateTime Time { get; set; }

        public long? Frame { get; set; }

        /// <summary>trigger, recovered, note, gap or end.</summary>
        public string Type { get; set; }

        /// <summary>Text of the mark, null when none.</summary>
        public string? Text { get; set; }
    }

    /// <summary>The likely cause of an incident and the evidence for it.</summary>
    public class AdvancedIncidentDiagnosis {
        /// <summary>
        /// clouds, dew, fieldJump, guideStarOnly, driftTooFast, mountNotMoving, calibrationMismatch, mountMoved, camera,
        /// periodicSpike or unclear. UIs localise by this code with <see cref="Parameters"/>; <see cref="Message"/> is English.
        /// </summary>
        public string Cause { get; set; }

        public string Message { get; set; }
        public Dictionary<string, object?> Parameters { get; set; } = new Dictionary<string, object?>();
        public List<AdvancedIncidentEvidence> Evidence { get; set; } = new List<AdvancedIncidentEvidence>();
    }

    /// <summary>One piece of evidence of a diagnosis.</summary>
    public class AdvancedIncidentEvidence {
        /// <summary>Stable code for localisation, with <see cref="Parameters"/>; <see cref="Message"/> is English.</summary>
        public string Code { get; set; }

        public string Message { get; set; }
        public Dictionary<string, object?> Parameters { get; set; } = new Dictionary<string, object?>();

        /// <summary>The frame it points at, null when it is about a stretch of frames.</summary>
        public long? Frame { get; set; }
    }

    /// <summary>Telemetry of one frame of an incident: star, error, pulses and mount state.</summary>
    public class AdvancedIncidentFrame {
        public long Frame { get; set; }

        /// <summary>UTC.</summary>
        public DateTime Timestamp { get; set; }

        /// <summary>Exposure, ms.</summary>
        public double ExposureMs { get; set; }

        /// <summary>Guider state (<see cref="AdvancedGuiderStates"/>): Calibrating, Guiding, LostLock, Reacquiring, Paused, ...</summary>
        public string State { get; set; }

        public bool Settling { get; set; }
        public bool Dithering { get; set; }
        public bool StarFound { get; set; }

        /// <summary>True when the primary star was not measured and its position was estimated from the other stars.</summary>
        public bool PrimaryEstimated { get; set; }

        /// <summary>Tracker status when the star was not found (e.g. LowSnr, Saturated, MassChanged, NotFound), else null.</summary>
        public string? LostStatus { get; set; }

        /// <summary>Lock and star position, sensor px; null when unknown.</summary>
        public double? LockX { get; set; }

        public double? LockY { get; set; }
        public double? StarX { get; set; }
        public double? StarY { get; set; }

        /// <summary>Star offset from the lock position on the camera axes, px.</summary>
        public double? Dx { get; set; }

        public double? Dy { get; set; }

        /// <summary>Mount-axis error in guide pixels and arcsec, and the total error in arcsec.</summary>
        public double? RaDistanceRaw { get; set; }

        public double? DecDistanceRaw { get; set; }
        public double? RaArcsec { get; set; }
        public double? DecArcsec { get; set; }
        public double? TotalArcsec { get; set; }

        /// <summary>RA pulse, ms, and its direction (East/West, empty without a pulse).</summary>
        public int RaDuration { get; set; }

        public string RaDirection { get; set; }

        /// <summary>Dec pulse, ms, and its direction (North/South, empty without a pulse).</summary>
        public int DecDuration { get; set; }

        public string DecDirection { get; set; }

        /// <summary>True when the pulse was cut to the maximum duration.</summary>
        public bool RaLimited { get; set; }

        public bool DecLimited { get; set; }
        public double? Snr { get; set; }

        /// <summary>Star mass (background-subtracted flux), ADU.</summary>
        public double? StarMass { get; set; }

        /// <summary>Half-flux diameter, px.</summary>
        public double? Hfd { get; set; }

        public List<AdvancedGuideStar> Stars { get; set; } = new List<AdvancedGuideStar>();

        /// <summary>Mount state when this frame was taken; null values are unknown.</summary>
        public bool? MountTracking { get; set; }

        public bool? MountSlewing { get; set; }
        public bool? MountParked { get; set; }
        public string? PierSide { get; set; }

        /// <summary>Mount position, hours and degrees; null when unknown.</summary>
        public double? RightAscensionHours { get; set; }

        public double? DeclinationDeg { get; set; }

        /// <summary>Calibration step of this frame (West, East, Backlash, North, South, ...), null when not calibrating.</summary>
        public string? CalibrationDirection { get; set; }

        public int? CalibrationStep { get; set; }

        /// <summary>Which images were kept for this frame.</summary>
        public bool HasContext { get; set; }

        public bool HasKey { get; set; }

        /// <summary>Number of star crops kept for this frame.</summary>
        public int Crops { get; set; }
    }

    /// <summary>A stored image of an incident frame: the binned field, the full-resolution key image or a star crop.</summary>
    public class AdvancedIncidentImage {
        public long Frame { get; set; }

        /// <summary>context, key or crop.</summary>
        public string Kind { get; set; }

        /// <summary>For crops: index of the star in the frame's star list; 0 otherwise.</summary>
        public int Star { get; set; }

        /// <summary>Top-left corner in sensor pixels (0 for context and key images).</summary>
        public int X0 { get; set; }

        public int Y0 { get; set; }

        /// <summary>Image size in its own pixels; one pixel covers <see cref="Binning"/> × <see cref="Binning"/> sensor pixels.</summary>
        public int Width { get; set; }

        public int Height { get; set; }
        public int Binning { get; set; }
        public int BitDepth { get; set; }

        /// <summary>Row-major 16-bit pixels.</summary>
        public ushort[] Pixels { get; set; }
    }

    /// <summary>Payload of the <see cref="AdvancedGuiderEventTypes.Incident"/> event.</summary>
    public class AdvancedIncidentEvent {
        /// <summary>started (recording began), saved (written; also after an ongoing incident was extended), deleted.</summary>
        public string Action { get; set; }

        public string Id { get; set; }

        /// <summary>The incident as a plain summary (never the full <see cref="AdvancedIncident"/>); null for deleted.</summary>
        public AdvancedIncidentSummary? Summary { get; set; }
    }
}
