// SPDX-License-Identifier: MPL-2.0

using System.Globalization;
using NINA.Astrometry;
using NINA.Core.Enum;
using NINA.Core.Utility;
using NINA.Mcp.Observatory;
using NINA.Sequencer;
using NINA.Sequencer.Conditions;
using NINA.Sequencer.Container;
using NINA.Sequencer.Interfaces;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.SequenceItem.FilterWheel;
using NINA.Sequencer.SequenceItem.Imaging;
using NINA.Sequencer.SequenceItem.Telescope;
using NINA.Sequencer.Trigger;
using NINA.Sequencer.Trigger.Guider;
using NINA.Sequencer.Validations;

namespace NINA.Mcp.Adapter.Sequence;

public sealed partial class NinaSequenceService {
    private const int MaxSnapshotDepth = 20;
    private const int MaxSavedSequences = 500;
    private const int MaxScannedSequenceFiles = 5000;

    /// <summary>
    /// Tree of the advanced sequence. <see cref="SequenceSnapshot.Nodes"/> holds one node, the root (path ""), whose
    /// children are the start, target and end areas ("0", "1", "2"); targets are "1/0", "1/1", ... The root counts as
    /// depth 1. Children of immutable blocks (Smart Exposure, Take Many Exposures, ...) are not expanded; their
    /// settings are in the block's details.
    /// </summary>
    public SequenceSnapshot GetSequence(int maxDepth) {
        try {
            SequencerAccess? access = TryGetAccess(out _);
            if (access == null) {
                return new SequenceSnapshot(false, false, null, Array.Empty<SequenceNode>());
            }

            int depth = Math.Clamp(maxDepth, 1, MaxSnapshotDepth);
            SequenceNode root = BuildNode(access.Root, string.Empty, depth);
            return new SequenceSnapshot(true, access.ViewModel.IsRunning, SafeTitle(access.Root), new[] { root });
        } catch (Exception ex) {
            Logger.Error("MCP: building the sequence snapshot failed", ex);
            return new SequenceSnapshot(false, IsRunning(), null, Array.Empty<SequenceNode>());
        }
    }

    public IReadOnlyList<SavedSequenceInfo> ListSavedSequences() {
        try {
            string? folder = SequenceFolder();
            if (folder == null || !Directory.Exists(folder)) {
                return Array.Empty<SavedSequenceInfo>();
            }

            var excludedFolders = new List<string>();
            AddExcludedFolder(excludedFolders, profileService.ActiveProfile?.SequenceSettings?.SequencerTemplatesFolder);
            AddExcludedFolder(excludedFolders, profileService.ActiveProfile?.SequenceSettings?.SequencerTargetsFolder);

            var result = new List<SavedSequenceInfo>();
            var options = new EnumerationOptions {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                MatchCasing = MatchCasing.CaseInsensitive
            };

            foreach (string file in Directory.EnumerateFiles(folder, "*.json", options)) {
                if (!IsSequenceFileCandidate(folder, file, excludedFolders)) {
                    continue;
                }

                try {
                    var info = new FileInfo(file);
                    result.Add(new SavedSequenceInfo(
                        ToSequenceName(folder, file),
                        Path.GetRelativePath(folder, file).Replace('\\', '/'),
                        new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)));
                } catch (Exception ex) {
                    Logger.Warning($"MCP: reading sequence file info for {file} failed: {ex.Message}");
                }

                if (result.Count >= MaxScannedSequenceFiles) {
                    Logger.Warning($"MCP: more than {MaxScannedSequenceFiles} sequence files below {folder}; listing stops there");
                    break;
                }
            }

            return result
                .OrderByDescending(info => info.LastModified)
                .Take(MaxSavedSequences)
                .ToList();
        } catch (Exception ex) {
            Logger.Error("MCP: listing saved sequences failed", ex);
            return Array.Empty<SavedSequenceInfo>();
        }
    }

    public IReadOnlyList<string> ListTargetTemplates() {
        try {
            if (!sequenceNavigation.Initialized) {
                return Array.Empty<string>();
            }

            return GetTemplates()
                .Select(template => template.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        } catch (Exception ex) {
            Logger.Error("MCP: listing target templates failed", ex);
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// The deep sky object container templates without the empty "Deep Sky Object Sequence" placeholder that
    /// <c>Sequence2VM.GetDeepSkyObjectContainerTemplates</c> puts first.
    /// </summary>
    private IReadOnlyList<IDeepSkyObjectContainer> GetTemplates() {
        IList<IDeepSkyObjectContainer>? all = sequenceMediator.GetDeepSkyObjectContainerTemplates();
        if (all == null || all.Count == 0) {
            return Array.Empty<IDeepSkyObjectContainer>();
        }

        var templates = new List<IDeepSkyObjectContainer>();
        for (int i = 0; i < all.Count; i++) {
            IDeepSkyObjectContainer? template = all[i];
            if (template == null) {
                continue;
            }

            bool isEmptyPlaceholder = i == 0
                && template.GetType() == typeof(DeepSkyObjectContainer)
                && template.GetItemsSnapshot().Count == 0;
            if (isEmptyPlaceholder) {
                continue;
            }

            templates.Add(template);
        }

        return templates;
    }

    private SequenceNode BuildNode(ISequenceItem item, string path, int remainingDepth) {
        var details = new Dictionary<string, object?>();
        AddDetails(item, details);

        // The root is an IImmutableContainer too, but its areas are what callers want to see.
        bool expandable = item is ISequenceRootContainer || item is not IImmutableContainer;

        var children = new List<SequenceNode>();
        if (item is ISequenceContainer container && expandable) {
            ICollection<ISequenceItem> items = container.GetItemsSnapshot();
            if (remainingDepth > 1) {
                int index = 0;
                foreach (ISequenceItem child in items) {
                    string childPath = path.Length == 0
                        ? index.ToString(CultureInfo.InvariantCulture)
                        : $"{path}/{index.ToString(CultureInfo.InvariantCulture)}";
                    children.Add(BuildNode(child, childPath, remainingDepth - 1));
                    index++;
                }
            } else if (items.Count > 0) {
                details["childrenOmitted"] = items.Count;
            }
        }

        return new SequenceNode(path, DisplayName(item), item.GetType().Name, FormatStatus(item.Status), details, children);
    }

    private static void AddDetails(ISequenceItem item, Dictionary<string, object?> details) {
        try {
            switch (item) {
                case IDeepSkyObjectContainer dso:
                    AddTargetDetails(dso, details);
                    break;

                case SmartExposure smartExposure:
                    AddSmartExposureDetails(smartExposure, details);
                    break;

                case TakeManyExposures takeMany:
                    AddExposureDetails(takeMany.GetTakeExposure(), details);
                    AddLoopDetails(takeMany.GetLoopCondition(), details);
                    break;

                case IExposureItem exposure:
                    AddExposureDetails(exposure, details);
                    break;

                case SwitchFilter switchFilter:
                    details["filter"] = switchFilter.ComboBoxText;
                    break;

                case CoordinatesInstruction coordinatesInstruction:
                    AddCoordinatesDetails(coordinatesInstruction, details);
                    break;
            }

            if (item is ISequenceContainer container) {
                AddConditionAndTriggerDetails(container, details);
            }

            IReadOnlyList<string> issues = SafeIssues(item);
            if (issues.Count > 0) {
                details["issues"] = issues;
            }
        } catch (Exception ex) {
            Logger.Debug($"MCP: reading details of {item.GetType().Name} failed: {ex.Message}");
            details["detailsError"] = ex.Message;
        }
    }

    private static void AddTargetDetails(IDeepSkyObjectContainer dso, Dictionary<string, object?> details) {
        InputTarget? target = dso.Target;
        if (target == null) {
            return;
        }

        details["targetName"] = target.TargetName;
        details["positionAngle"] = Finite(target.PositionAngle);

        Coordinates? coordinates = target.InputCoordinates?.Coordinates;
        if (coordinates == null) {
            return;
        }

        Coordinates j2000 = coordinates.Epoch == Epoch.J2000 ? coordinates : coordinates.Transform(Epoch.J2000);
        details["raHours"] = Finite(j2000.RA);
        details["decDegrees"] = Finite(j2000.Dec);
        details["ra"] = AstroUtil.HoursToHMS(j2000.RA);
        details["dec"] = AstroUtil.DegreesToDMS(j2000.Dec);
    }

    private static void AddSmartExposureDetails(SmartExposure smartExposure, Dictionary<string, object?> details) {
        SwitchFilter? switchFilter = smartExposure.GetSwitchFilter();
        if (switchFilter != null) {
            details["filter"] = switchFilter.ComboBoxText;
        }

        AddExposureDetails(smartExposure.GetTakeExposure(), details);
        AddLoopDetails(smartExposure.GetLoopCondition(), details);

        DitherAfterExposures? dither = smartExposure.GetDitherAfterExposures();
        if (dither != null) {
            details["ditherEvery"] = dither.AfterExposures;
        }
    }

    private static void AddExposureDetails(IExposureItem? exposure, Dictionary<string, object?> details) {
        if (exposure == null) {
            return;
        }

        details["exposureSeconds"] = Finite(exposure.ExposureTime);
        // -1 means "camera default" in NINA.
        details["gain"] = exposure.Gain < 0 ? null : (object)exposure.Gain;
        details["offset"] = exposure.Offset < 0 ? null : (object)exposure.Offset;
        details["binning"] = exposure.Binning?.Name;
        details["imageType"] = exposure.ImageType;

        if (exposure is TakeExposure takeExposure) {
            details["exposureCount"] = takeExposure.ExposureCount;
        }
    }

    private static void AddLoopDetails(LoopCondition? loop, Dictionary<string, object?> details) {
        if (loop == null) {
            return;
        }

        details["iterations"] = loop.Iterations;
        details["completedIterations"] = loop.CompletedIterations;
    }

    private static void AddCoordinatesDetails(CoordinatesInstruction instruction, Dictionary<string, object?> details) {
        Coordinates? coordinates = instruction.Coordinates?.Coordinates;
        if (coordinates != null) {
            details["raHours"] = Finite(coordinates.RA);
            details["decDegrees"] = Finite(coordinates.Dec);
        }

        details["inheritedFromTarget"] = instruction.Inherited;
    }

    private static void AddConditionAndTriggerDetails(ISequenceContainer container, Dictionary<string, object?> details) {
        if (container is IConditionable conditionable) {
            ICollection<ISequenceCondition> conditions = conditionable.GetConditionsSnapshot();
            if (conditions.Count > 0) {
                details["conditions"] = conditions.Select(SummarizeEntity).ToList();
            }
        }

        if (container is ITriggerable triggerable) {
            ICollection<ISequenceTrigger> triggers = triggerable.GetTriggersSnapshot();
            if (triggers.Count > 0) {
                details["triggers"] = triggers.Select(SummarizeEntity).ToList();
            }
        }
    }

    private static Dictionary<string, object?> SummarizeEntity(ISequenceEntity entity) {
        var summary = new Dictionary<string, object?> {
            ["name"] = DisplayName(entity),
            ["type"] = entity.GetType().Name,
            ["status"] = FormatStatus(entity.Status)
        };

        try {
            summary["summary"] = entity.ToString();
        } catch (Exception ex) {
            summary["summary"] = null;
            Logger.Debug($"MCP: summarizing {entity.GetType().Name} failed: {ex.Message}");
        }

        IReadOnlyList<string> issues = SafeIssues(entity);
        if (issues.Count > 0) {
            summary["issues"] = issues;
        }

        return summary;
    }

    private static IReadOnlyList<string> SafeIssues(ISequenceEntity entity) {
        if (entity is not IValidatable validatable) {
            return Array.Empty<string>();
        }

        try {
            IList<string>? issues = validatable.Issues;
            if (issues == null || issues.Count == 0) {
                return Array.Empty<string>();
            }

            return issues.ToArray();
        } catch (Exception) {
            // The background validation of NINA may replace the list while it is read; skip it this time.
            return Array.Empty<string>();
        }
    }

    private static string FormatStatus(SequenceEntityStatus status) {
        string name = status.ToString();
        if (name.Length == 0) {
            return name;
        }

        return char.ToUpperInvariant(name[0]) + name.Substring(1).ToLowerInvariant();
    }

    private static object? Finite(double value) {
        if (double.IsNaN(value) || double.IsInfinity(value)) {
            return null;
        }

        return value;
    }

    private static string? SafeTitle(ISequenceRootContainer root) {
        try {
            return root.SequenceTitle;
        } catch (Exception) {
            return null;
        }
    }

    private string? SequenceFolder() {
        string? folder = profileService.ActiveProfile?.SequenceSettings?.DefaultSequenceFolder;
        if (string.IsNullOrWhiteSpace(folder)) {
            return null;
        }

        return Path.GetFullPath(folder);
    }

    private static void AddExcludedFolder(List<string> excludedFolders, string? folder) {
        if (string.IsNullOrWhiteSpace(folder)) {
            return;
        }

        try {
            excludedFolders.Add(WithTrailingSeparator(Path.GetFullPath(folder)));
        } catch (Exception ex) {
            Logger.Debug($"MCP: ignoring invalid folder setting '{folder}': {ex.Message}");
        }
    }

    private static bool IsSequenceFileCandidate(string folder, string file, List<string> excludedFolders) {
        if (file.EndsWith(".template.json", StringComparison.OrdinalIgnoreCase)) {
            return false;
        }

        foreach (string excluded in excludedFolders) {
            if (file.StartsWith(excluded, StringComparison.OrdinalIgnoreCase)) {
                return false;
            }
        }

        // Autofocus reports may live below the sequence folder (Touch-N-Stars filters them the same way).
        string relative = Path.GetRelativePath(folder, file);
        return !relative.Contains("AutoFocus", StringComparison.OrdinalIgnoreCase);
    }

    private static string ToSequenceName(string folder, string file) {
        string relative = Path.GetRelativePath(folder, file).Replace('\\', '/');
        return relative.Substring(0, relative.Length - ".json".Length);
    }

    private static string WithTrailingSeparator(string path) {
        if (path.EndsWith(Path.DirectorySeparatorChar)) {
            return path;
        }

        return path + Path.DirectorySeparatorChar;
    }
}
