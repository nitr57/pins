// SPDX-License-Identifier: MPL-2.0

using NINA.Mcp.Observatory;

namespace NINA.Mcp.Tools;

/// <summary>
/// Checks on a sequence snapshot that the model should hear about before and while the sequence runs. A target node is
/// one whose details carry <c>targetName</c> (a deep sky object container); a trigger on the target or any container
/// above it covers it.
/// </summary>
internal static class SequenceChecks {

    /// <summary>Depth of the snapshot the checks read: deep enough for targets inside nested containers.</summary>
    public const int Depth = 10;

    private const string MeridianFlipTrigger = "MeridianFlipTrigger";

    /// <summary>Names of the targets that no meridian flip trigger covers, in sequence order.</summary>
    public static IReadOnlyList<string> TargetsWithoutMeridianFlip(SequenceSnapshot snapshot) {
        ArgumentNullException.ThrowIfNull(snapshot);

        var targets = new List<string>();

        foreach (var node in snapshot.Nodes) {
            Visit(node, false, targets);
        }

        return targets;
    }

    /// <summary>A note for the model when a target has no meridian flip trigger; null when every target has one.</summary>
    public static string? MeridianFlipNote(SequenceSnapshot snapshot) {
        var targets = TargetsWithoutMeridianFlip(snapshot);

        if (targets.Count == 0) {
            return null;
        }

        return $"No meridian flip trigger covers {string.Join(", ", targets)}. On a German equatorial mount that crosses the "
            + "meridian during the target, add one (add_target adds it unless meridian_flip was false or no mount was connected "
            + "when the target was added); fork and alt-az mounts do not need it.";
    }

    private static void Visit(SequenceNode node, bool covered, List<string> targets) {
        covered |= HasMeridianFlipTrigger(node);

        if (!covered && node.Details.TryGetValue("targetName", out var targetName)) {
            targets.Add(targetName as string is { Length: > 0 } name ? name : node.Name);
        }

        foreach (var child in node.Children) {
            Visit(child, covered, targets);
        }
    }

    private static bool HasMeridianFlipTrigger(SequenceNode node) {
        if (!node.Details.TryGetValue("triggers", out var triggers) || triggers is not IEnumerable<object?> list) {
            return false;
        }

        return list
            .OfType<IReadOnlyDictionary<string, object?>>()
            .Any(trigger => trigger.TryGetValue("type", out var type) && type as string == MeridianFlipTrigger);
    }
}
