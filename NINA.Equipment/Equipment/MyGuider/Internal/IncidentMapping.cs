// SPDX-License-Identifier: MPL-2.0

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NINA.Equipment.Equipment.MyGuider.Advanced;
using NINA.Equipment.Interfaces;
using NINA.GuideEngine.Core;
using NINA.GuideEngine.Guiding;
using NINA.GuideEngine.Incidents;

namespace NINA.Equipment.Equipment.MyGuider.Internal;

/// <summary>
/// Maps flight recorder records to the <see cref="IGuideIncidentRecorder"/> DTOs (docs/INCIDENTS.md §3): kinds PascalCase, causes with
/// a lower-case first letter, end reasons and marker types lower case, FramesOmitted null | diskSpace | budget.
/// </summary>
internal static class IncidentMapping
{
    public static AdvancedIncidentSummary ToSummary(Incident i) => Fill(new AdvancedIncidentSummary(), i);

    public static AdvancedIncident ToDto(Incident i)
    {
        var dto = Fill(new AdvancedIncident(), i);
        double scale = i.Tags.PixelScale > 0 ? i.Tags.PixelScale : 1.0;
        dto.Triggers = i.Triggers.Select(t => new AdvancedIncidentTrigger
        {
            Time = t.Time.UtcDateTime,
            Kind = t.Kind.ToString(),
            Code = t.Code is { } c ? (int)c : null,
            CodeName = t.Code?.ToString(),
            Message = t.Message,
            Detail = t.Detail,
            Frame = t.Frame,
        }).ToList();
        dto.Markers = i.Markers.Select(m => new AdvancedIncidentMarker
        {
            Time = m.Time.UtcDateTime,
            Frame = m.Frame,
            Type = m.Type.ToString().ToLowerInvariant(),
            Text = m.Text,
        }).ToList();
        dto.Diagnosis = i.Diagnosis is { } d ? ToDto(d) : null;
        dto.SensorWidth = i.SensorWidth;
        dto.SensorHeight = i.SensorHeight;
        dto.ContextBinning = i.ContextBinning;
        dto.SearchRegionPx = i.Context.SearchRegionPx;
        dto.Frames = i.Frames.Select(f => ToDto(f, scale)).ToList();
        return dto;
    }

    /// <summary>Summary of an incident that just started recording (the engine event carries only id and kind).</summary>
    public static AdvancedIncidentSummary Started(IncidentStartedEvent e, IncidentTags tags) => new()
    {
        Id = e.Id,
        Start = e.Timestamp.UtcDateTime,
        End = e.Timestamp.UtcDateTime,
        Kind = e.Kind.ToString(),
        Kinds = [e.Kind.ToString()],
        Occurrences = 1,
        EndReason = EndReason(IncidentEndReason.Recording),
        Tags = ToDto(tags),
    };

    public static AdvancedIncidentDiagnosis ToDto(IncidentDiagnosis d) => new()
    {
        Cause = Cause(d.Cause),
        Message = d.Message,
        Parameters = Parameters(d.Parameters),
        Evidence = d.Evidence.Select(e => new AdvancedIncidentEvidence
        {
            Code = e.Code,
            Message = e.Message,
            Parameters = Parameters(e.Parameters),
            Frame = e.Frame,
        }).ToList(),
    };

    public static AdvancedIncidentFrame ToDto(IncidentFrameRecord f, double pixelScale)
    {
        var m = f.Mount is { IsConnected: true } snapshot ? snapshot : null;
        return new AdvancedIncidentFrame
        {
            Frame = f.Frame,
            Timestamp = f.Time.UtcDateTime,
            ExposureMs = f.ExposureMs,
            State = f.State.ToString(),
            Settling = f.Settling,
            Dithering = f.Dithering,
            StarFound = f.StarFound,
            PrimaryEstimated = f.PrimaryEstimated,
            LostStatus = f.LostStatus,
            LockX = f.Lock?.X,
            LockY = f.Lock?.Y,
            StarX = f.Star?.X,
            StarY = f.Star?.Y,
            Dx = f.Dx,
            Dy = f.Dy,
            RaDistanceRaw = f.RaDistanceRaw,
            DecDistanceRaw = f.DecDistanceRaw,
            RaArcsec = f.RaDistanceRaw * pixelScale,
            DecArcsec = f.DecDistanceRaw * pixelScale,
            TotalArcsec = f.RaDistanceRaw is { } ra && f.DecDistanceRaw is { } dec ? Math.Sqrt(ra * ra + dec * dec) * pixelScale : null,
            RaDuration = f.RaDurationMs,
            RaDirection = f.RaDurationMs > 0 ? f.RaDirection?.ToString() ?? string.Empty : string.Empty,
            DecDuration = f.DecDurationMs,
            DecDirection = f.DecDurationMs > 0 ? f.DecDirection?.ToString() ?? string.Empty : string.Empty,
            RaLimited = f.RaLimited,
            DecLimited = f.DecLimited,
            Snr = f.Snr,
            StarMass = f.StarMass,
            Hfd = f.Hfd,
            Stars = f.Stars.Select(s => new AdvancedGuideStar
            {
                X = s.X,
                Y = s.Y,
                Snr = s.Snr,
                Mass = s.Mass,
                Hfd = s.Hfd,
                IsPrimary = s.IsPrimary,
                Used = s.Used,
                Weight = s.Weight,
                RejectReason = s.RejectReason,
            }).ToList(),
            MountTracking = m?.IsTracking,
            MountSlewing = m?.IsSlewing,
            MountParked = m?.IsParked,
            PierSide = m is { PierSide: not PierSide.Unknown } ? m.PierSide.ToString() : null,
            RightAscensionHours = m?.RightAscensionHours,
            DeclinationDeg = m?.DeclinationDeg,
            CalibrationDirection = f.CalibrationDirection,
            CalibrationStep = f.CalibrationStep,
            HasContext = f.HasContext,
            HasKey = f.HasKey,
            Crops = f.Crops,
        };
    }

    public static AdvancedIncidentImage ToDto(IncidentImage img) => new()
    {
        Frame = img.Frame,
        Kind = img.Kind.ToString().ToLowerInvariant(),
        Star = img.Kind == IncidentImageKind.Crop ? img.Star : 0,
        X0 = img.Kind == IncidentImageKind.Crop ? img.X0 : 0,
        Y0 = img.Kind == IncidentImageKind.Crop ? img.Y0 : 0,
        Width = img.Width,
        Height = img.Height,
        Binning = img.Binning,
        BitDepth = 16,
        Pixels = img.Pixels,
    };

    public static AdvancedIncidentTags ToDto(IncidentTags t) => new()
    {
        ProfileId = t.ProfileId,
        ProfileName = t.ProfileName,
        GuideCamera = t.GuideCamera,
        Mount = t.Mount,
        Simulator = t.Simulator,
        PixelScale = t.PixelScale,
        ImagingScale = t.ImagingScale,
    };

    /// <summary>"context" or "key" (case-insensitive) as an image kind; null for anything else.</summary>
    public static IncidentImageKind? ImageKind(string? kind) => kind?.Trim().ToLowerInvariant() switch
    {
        "context" => IncidentImageKind.Context,
        "key" => IncidentImageKind.Key,
        _ => null,
    };

    public static string Cause(IncidentCause c)
    {
        string s = c.ToString();
        return char.ToLowerInvariant(s[0]) + s[1..];
    }

    public static string EndReason(IncidentEndReason r) => r.ToString().ToLowerInvariant();

    public static string? FramesOmitted(IncidentFramesOmitted o) => o switch
    {
        IncidentFramesOmitted.DiskSpace => "diskSpace",
        IncidentFramesOmitted.Budget => "budget",
        _ => null,
    };

    private static T Fill<T>(T dto, Incident i)
        where T : AdvancedIncidentSummary
    {
        dto.Id = i.Id;
        dto.Start = i.Start.UtcDateTime;
        dto.End = i.End.UtcDateTime;
        dto.Kind = i.Kind.ToString();
        dto.Kinds = i.Triggers.Select(t => t.Kind.ToString()).Distinct().ToList();
        if (dto.Kinds.Count == 0)
        {
            dto.Kinds.Add(dto.Kind);
        }

        dto.Occurrences = i.Occurrences;
        dto.Ongoing = i.Ongoing;
        dto.EndReason = EndReason(i.EndReason);
        dto.Kept = i.Kept;
        dto.SizeBytes = i.SizeBytes;
        dto.FrameCount = i.FrameCount > 0 ? i.FrameCount : i.Frames.Count;
        dto.FramesOmitted = FramesOmitted(i.FramesOmitted);
        dto.Note = i.Note;
        dto.Cause = i.Diagnosis is { } d ? Cause(d.Cause) : null;
        dto.Tags = ToDto(i.Tags);
        return dto;
    }

    private static Dictionary<string, object?> Parameters(IReadOnlyDictionary<string, object?> p) => new(p);
}
