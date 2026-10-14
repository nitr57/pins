// SPDX-License-Identifier: MPL-2.0

using NINA.Mcp.Observatory;
using NINA.Mcp.Test.Fakes;
using NINA.Mcp.Tools;
using NUnit.Framework;

namespace NINA.Mcp.Test;

/// <summary>Targets that no meridian flip trigger covers, flagged by get_sequence and start_sequence.</summary>
[TestFixture]
public class SequenceChecksTests {

    [Test]
    public void TriggerOnTheTargetOrAnAncestor_CoversIt() {
        var snapshot = Snapshot(
            Container("Sequence", [],
                Container("Targets", [],
                    Target("M 31", "MeridianFlipTrigger"),
                    Target("M 33"),
                    Container("Late", ["MeridianFlipTrigger"], Target("M 42")))));

        Assert.That(SequenceChecks.TargetsWithoutMeridianFlip(snapshot), Is.EqualTo(new[] { "M 33" }));
        Assert.That(SequenceChecks.MeridianFlipNote(snapshot), Does.Contain("M 33").And.Contain("German equatorial"));
    }

    [Test]
    public void RootTrigger_CoversEveryTarget() {
        var snapshot = Snapshot(Container("Sequence", ["MeridianFlipTrigger"], Target("M 31"), Target("M 33")));

        Assert.That(SequenceChecks.TargetsWithoutMeridianFlip(snapshot), Is.Empty);
        Assert.That(SequenceChecks.MeridianFlipNote(snapshot), Is.Null);
    }

    [Test]
    public void OtherTriggers_DoNotCount() {
        var snapshot = Snapshot(Container("Sequence", ["AutofocusAfterHFRIncreaseTrigger"], Target("M 31", "DitherAfterExposures")));

        Assert.That(SequenceChecks.TargetsWithoutMeridianFlip(snapshot), Is.EqualTo(new[] { "M 31" }));
    }

    [Test]
    public async Task GetSequenceAndStartSequence_ReportTheNote() {
        using var catalog = new ToolsTestCatalog();
        catalog.Observatory.Sequence = Snapshot(Container("Sequence", [], Target("NGC 7000")));

        var sequence = await catalog.CallAsync("get_sequence");
        var start = await catalog.CallAsync("start_sequence");

        Assert.That(sequence.Structured!["name"]!.GetValue<string>(), Is.EqualTo("Fake sequence"));
        Assert.That(sequence.Structured!["notes"]![0]!.GetValue<string>(), Does.Contain("NGC 7000"));
        Assert.That(start.IsError, Is.False, start.Text);
        Assert.That(start.Structured!["message"]!.GetValue<string>(), Does.StartWith("StartSequence done").And.Contain("NGC 7000"));
    }

    [Test]
    public async Task CoveredSequence_HasNoNotes() {
        using var catalog = new ToolsTestCatalog();
        catalog.Observatory.Sequence = Snapshot(Container("Sequence", [], Target("NGC 7000", "MeridianFlipTrigger")));

        var sequence = await catalog.CallAsync("get_sequence");
        var start = await catalog.CallAsync("start_sequence");

        Assert.That(sequence.Structured!["notes"], Is.Null);
        Assert.That(start.Structured!["message"]!.GetValue<string>(), Is.EqualTo("StartSequence done"));
    }

    private static SequenceSnapshot Snapshot(SequenceNode root) {
        return new SequenceSnapshot(true, false, "Fake sequence", [root]);
    }

    private static SequenceNode Target(string name, params string[] triggers) {
        var details = Triggers(triggers);
        details["targetName"] = name;

        return new SequenceNode("0", name, "DeepSkyObjectContainer", "Created", details, []);
    }

    private static SequenceNode Container(string name, string[] triggers, params SequenceNode[] children) {
        return new SequenceNode("0", name, "SequentialContainer", "Created", Triggers(triggers), children);
    }

    private static Dictionary<string, object?> Triggers(string[] types) {
        var details = new Dictionary<string, object?>();

        if (types.Length > 0) {
            details["triggers"] = types
                .Select(type => new Dictionary<string, object?> { ["name"] = type, ["type"] = type, ["status"] = "Created" })
                .ToList();
        }

        return details;
    }
}
