// SPDX-License-Identifier: MPL-2.0

using System.Globalization;
using NINA.Core.Model.Equipment;
using NINA.Core.Utility;
using NINA.Mcp.Observatory;
using NINA.Sequencer;
using NINA.Sequencer.Conditions;
using NINA.Sequencer.Container;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.SequenceItem.Autofocus;
using NINA.Sequencer.SequenceItem.FilterWheel;
using NINA.Sequencer.SequenceItem.Guider;
using NINA.Sequencer.SequenceItem.Imaging;
using NINA.Sequencer.SequenceItem.Platesolving;
using NINA.Sequencer.Trigger.Guider;

namespace NINA.Mcp.Adapter.Sequence;

/// <summary>
/// Builds a deep sky object container from an exposure plan with the sequencer factory, the same way
/// <c>SimpleExposure.TransformToSmartExposure</c> builds Smart Exposures in NINA.
/// </summary>
public sealed partial class NinaSequenceService {
    private const int MaxExposureCount = 100000;
    private const int MaxBinning = 4;

    // Range of DitherAfterExposures.AfterExposures ([IsExpression(Range = [0, 32])]).
    private const int MaxDitherEvery = 32;

    private const string NoRotatorId = "No_Device";

    private static string? ValidateTargetRequest(TargetRequest request) {
        if (string.IsNullOrWhiteSpace(request.Name)) {
            return "The target needs a name.";
        }

        if (!double.IsFinite(request.RaHours) || request.RaHours < 0 || request.RaHours >= 24) {
            return "RA must be in hours, 0 <= RA < 24.";
        }

        if (!double.IsFinite(request.DecDegrees) || request.DecDegrees < -90 || request.DecDegrees > 90) {
            return "Dec must be in degrees, -90 <= Dec <= 90.";
        }

        if (request.RotationDegrees.HasValue && !double.IsFinite(request.RotationDegrees.Value)) {
            return "The rotation must be a finite number of degrees.";
        }

        bool hasTemplate = !string.IsNullOrWhiteSpace(request.TemplateName);
        bool hasPlan = request.Exposures != null && request.Exposures.Count > 0;
        if (hasTemplate == hasPlan) {
            return "Pass exactly one of a target template or an exposure plan.";
        }

        if (hasTemplate) {
            return null;
        }

        if (request.DitherEvery < 0 || request.DitherEvery > MaxDitherEvery) {
            return $"dither_every must be between 0 and {MaxDitherEvery} (the range NINA's Dither After Exposures accepts).";
        }

        for (int i = 0; i < request.Exposures!.Count; i++) {
            string? itemError = ValidatePlanItem(request.Exposures[i]);
            if (itemError != null) {
                return $"Exposure block {i + 1}: {itemError}";
            }
        }

        return null;
    }

    private static string? ValidatePlanItem(ExposurePlanItem? item) {
        if (item == null) {
            return "missing.";
        }

        if (!double.IsFinite(item.ExposureSeconds) || item.ExposureSeconds <= 0 || item.ExposureSeconds > 3600) {
            return "the exposure time must be greater than 0 and at most 3600 seconds.";
        }

        if (item.Count < 1 || item.Count > MaxExposureCount) {
            return $"the count must be between 1 and {MaxExposureCount}.";
        }

        if (item.Gain.HasValue && item.Gain.Value < 0) {
            return "the gain must not be negative.";
        }

        if (item.Offset.HasValue && item.Offset.Value < 0) {
            return "the offset must not be negative.";
        }

        if (item.Binning.HasValue && (item.Binning.Value < 1 || item.Binning.Value > MaxBinning)) {
            return $"the binning must be between 1 and {MaxBinning}.";
        }

        return null;
    }

    /// <summary>Runs on the dispatcher. Builds the target container and appends it to the target area.</summary>
    private OperationResult AddTargetFromPlan(SequencerAccess access, TargetRequest request) {
        IReadOnlyList<ExposurePlanItem> plan = request.Exposures!;

        // Resolve everything that can fail before the first entity is created.
        string?[] filterNames = new string?[plan.Count];
        for (int i = 0; i < plan.Count; i++) {
            string? requested = plan[i].Filter;
            if (string.IsNullOrWhiteSpace(requested)) {
                continue;
            }

            string? resolved = ResolveFilterName(requested, out string filterError);
            if (resolved == null) {
                return OperationResult.Fail($"Exposure block {i + 1}: {filterError}");
            }

            filterNames[i] = resolved;
        }

        ISequencerFactory factory = access.Factory;
        DeepSkyObjectContainer? container = factory.GetContainer<DeepSkyObjectContainer>();
        if (container == null) {
            return OperationResult.Fail("The sequencer factory provides no Deep Sky Object container.");
        }

        var steps = new List<string>();

        if (request.SlewAndCenter) {
            bool rotate = request.RotationDegrees.HasValue && IsRotatorAvailable();
            ISequenceItem? center = rotate
                ? factory.GetItem<CenterAndRotate>()
                : factory.GetItem<Center>();
            if (center == null) {
                return OperationResult.Fail("The sequencer factory provides no Center instruction.");
            }

            container.Add(center);
            steps.Add(rotate ? "slew, center and rotate" : "slew and center");
        }

        if (request.AutofocusAtStart) {
            RunAutofocus? autofocus = factory.GetItem<RunAutofocus>();
            if (autofocus == null) {
                return OperationResult.Fail("The sequencer factory provides no Run Autofocus instruction.");
            }

            container.Add(autofocus);
            steps.Add("autofocus");
        }

        if (request.StartGuiding) {
            StartGuiding? startGuiding = factory.GetItem<StartGuiding>();
            if (startGuiding == null) {
                return OperationResult.Fail("The sequencer factory provides no Start Guiding instruction.");
            }

            startGuiding.ForceCalibration = false;
            container.Add(startGuiding);
            steps.Add("start guiding");
        }

        for (int i = 0; i < plan.Count; i++) {
            SmartExposure? smartExposure = CreateSmartExposure(factory, plan[i], filterNames[i], request.DitherEvery, out string error);
            if (smartExposure == null) {
                return OperationResult.Fail($"Exposure block {i + 1}: {error}");
            }

            container.Add(smartExposure);
            steps.Add(DescribePlanItem(plan[i], filterNames[i]));
        }

        ApplyTarget(container, request);
        sequenceMediator.AddAdvancedTarget(container);

        Logger.Info($"MCP: added target '{request.Name}' built from an exposure plan with {plan.Count} block(s)");

        string rotationNote = string.Empty;
        if (request.SlewAndCenter && request.RotationDegrees.HasValue && !IsRotatorAvailable()) {
            rotationNote = " No rotator is configured, so the position angle is stored on the target but not applied.";
        }

        string ditherNote = request.DitherEvery > 0
            ? $" Dithering every {request.DitherEvery} frame{(request.DitherEvery == 1 ? string.Empty : "s")}."
            : string.Empty;

        return OperationResult.Ok(
            $"Added target '{request.Name.Trim()}' at the end of the target area (RA {request.RaHours:0.#####} h, "
            + $"Dec {request.DecDegrees:0.#####}°): {string.Join(", then ", steps)}.{ditherNote}{rotationNote}"
            + FormatIssueHint(container));
    }

    private static SmartExposure? CreateSmartExposure(
            ISequencerFactory factory,
            ExposurePlanItem item,
            string? filterName,
            int ditherEvery,
            out string error) {
        error = string.Empty;

        SmartExposure? smartExposure = factory.GetItem<SmartExposure>();
        if (smartExposure == null) {
            error = "the sequencer factory provides no Smart Exposure.";
            return null;
        }

        SwitchFilter? switchFilter = smartExposure.GetSwitchFilter();
        TakeExposure? takeExposure = smartExposure.GetTakeExposure();
        LoopCondition? loop = smartExposure.GetLoopCondition();
        DitherAfterExposures? dither = smartExposure.GetDitherAfterExposures();
        if (switchFilter == null || takeExposure == null || loop == null || dither == null) {
            error = "the Smart Exposure prototype is incomplete.";
            return null;
        }

        // "(Current)" keeps the filter that is in place, like a new Smart Exposure in NINA's editor.
        switchFilter.ComboBoxText = filterName ?? NINA.Core.Model.NullFilter.Instance.Name;

        takeExposure.ImageType = NINA.Equipment.Model.CaptureSequence.ImageTypes.LIGHT;
        takeExposure.ExposureTime = item.ExposureSeconds;
        if (item.Gain.HasValue) {
            takeExposure.Gain = item.Gain.Value;
        }

        if (item.Offset.HasValue) {
            takeExposure.Offset = item.Offset.Value;
        }

        short binning = (short)(item.Binning ?? 1);
        takeExposure.Binning = new BinningMode(binning, binning);

        // Both are set: the Smart Exposure's own expression and its internal loop condition (which the expression
        // validator only updates when the value changes, and which defaults to 2 iterations).
        loop.CompletedIterations = 0;
        loop.Iterations = item.Count;
        smartExposure.Iterations = item.Count;

        // Dither After Exposures defaults to 3; 0 disables it.
        dither.AfterExposures = ditherEvery;

        return smartExposure;
    }

    private string? ResolveFilterName(string requested, out string error) {
        error = string.Empty;

        IList<FilterInfo>? filters = profileService.ActiveProfile?.FilterWheelSettings?.FilterWheelFilters?.ToList();
        if (filters == null || filters.Count == 0) {
            error = $"the filter '{requested}' cannot be used: the active profile has no filters configured.";
            return null;
        }

        string wanted = requested.Trim();
        FilterInfo? match = filters.FirstOrDefault(filter => string.Equals(filter?.Name, wanted, StringComparison.Ordinal))
            ?? filters.FirstOrDefault(filter => string.Equals(filter?.Name, wanted, StringComparison.OrdinalIgnoreCase));
        if (match == null || string.IsNullOrWhiteSpace(match.Name)) {
            string available = string.Join(", ", filters
                .Where(filter => filter != null && !string.IsNullOrWhiteSpace(filter.Name))
                .Select(filter => $"'{filter.Name}'"));
            error = $"no filter named '{requested}'. Configured filters: {available}.";
            return null;
        }

        return match.Name;
    }

    private bool IsRotatorAvailable() {
        try {
            if (rotatorMediator.GetInfo()?.Connected == true) {
                return true;
            }

            string? id = profileService.ActiveProfile?.RotatorSettings?.Id;
            return !string.IsNullOrWhiteSpace(id) && !string.Equals(id, NoRotatorId, StringComparison.Ordinal);
        } catch (Exception ex) {
            Logger.Debug($"MCP: reading the rotator state failed: {ex.Message}");
            return false;
        }
    }

    private static string DescribePlanItem(ExposurePlanItem item, string? filterName) {
        string description = string.Format(
            CultureInfo.InvariantCulture,
            "{0}x {1:0.####} s {2}",
            item.Count,
            item.ExposureSeconds,
            filterName ?? "(current filter)");

        var settings = new List<string>();
        if (item.Gain.HasValue) {
            settings.Add($"gain {item.Gain.Value.ToString(CultureInfo.InvariantCulture)}");
        }

        if (item.Offset.HasValue) {
            settings.Add($"offset {item.Offset.Value.ToString(CultureInfo.InvariantCulture)}");
        }

        if (item.Binning.HasValue && item.Binning.Value != 1) {
            settings.Add($"bin {item.Binning.Value}x{item.Binning.Value}");
        }

        if (settings.Count == 0) {
            return description;
        }

        return $"{description} ({string.Join(", ", settings)})";
    }
}
