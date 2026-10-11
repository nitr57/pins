// SPDX-License-Identifier: MPL-2.0

using NINA.Mcp.Observatory;
using NINA.Mcp.Test.Fakes;
using NINA.Mcp.Tools.Catalog;
using NUnit.Framework;

namespace NINA.Mcp.Test;

/// <summary>Sequence tools: add_target, save_sequence, load_sequence and friends.</summary>
[TestFixture]
public class ToolsSequenceTests {

    private const string Plan = """[{"exposure_seconds":300,"count":20,"filter":"Ha","gain":100,"offset":50,"binning":1},{"exposure_seconds":60,"count":5}]""";

    private ToolsTestCatalog catalog = null!;

    [SetUp]
    public void SetUp() {
        catalog = new ToolsTestCatalog();
        catalog.Observatory.Templates.AddRange(["Narrowband Template", "LRGB"]);
        catalog.Observatory.SavedSequences.Add(new SavedSequenceInfo("Orion Night", "Orion Night.json", DateTimeOffset.UnixEpoch));
    }

    [TearDown]
    public void TearDown() {
        catalog.Dispose();
    }

    // ----- add_target -----

    [Test]
    public async Task AddTarget_ExposurePlan_BuildsRequestWithDefaults() {
        var result = await catalog.CallAsync("add_target", $$"""{"name":" M 42 ","ra":"05h35m17.3s","dec":"-05°23'28\"","exposures":{{Plan}}}""");

        Assert.That(result.IsError, Is.False, result.Text);

        var request = catalog.Observatory.LastTarget!;

        Assert.That(request.Name, Is.EqualTo("M 42"));
        Assert.That(request.RaHours, Is.EqualTo(5 + (35 / 60.0) + (17.3 / 3600)).Within(1e-9));
        Assert.That(request.DecDegrees, Is.EqualTo(-(5 + (23 / 60.0) + (28 / 3600.0))).Within(1e-9));
        Assert.That(request.TemplateName, Is.Null);
        Assert.That(request.RotationDegrees, Is.Null);
        Assert.That(request.SlewAndCenter, Is.True);
        Assert.That(request.StartGuiding, Is.True);
        Assert.That(request.AutofocusAtStart, Is.False);
        Assert.That(request.DitherEvery, Is.EqualTo(0));
        Assert.That(request.Exposures, Is.EqualTo(new[] {
            new ExposurePlanItem(300, 20, "Ha", 100, 50, 1),
            new ExposurePlanItem(60, 5)
        }));

        Assert.That(result.Structured!["target"]!["ra"]!.GetValue<string>(), Is.EqualTo("05h35m17.3s"));
        Assert.That(result.Structured["target"]!["dec"]!.GetValue<string>(), Is.EqualTo("-05°23'28\""));
    }

    [Test]
    public async Task AddTarget_ExposurePlan_PassesOptions() {
        await catalog.CallAsync(
            "add_target",
            """{"name":"NGC 7000","ra":20.98,"dec":44.5,"rotation_degrees":90,"exposures":[{"exposure_seconds":120,"count":1}],"slew_and_center":false,"start_guiding":false,"autofocus_at_start":true,"dither_every":3}""");

        var request = catalog.Observatory.LastTarget!;

        Assert.That(request.RotationDegrees, Is.EqualTo(90));
        Assert.That(request.SlewAndCenter, Is.False);
        Assert.That(request.StartGuiding, Is.False);
        Assert.That(request.AutofocusAtStart, Is.True);
        Assert.That(request.DitherEvery, Is.EqualTo(3));
    }

    [Test]
    public async Task AddTarget_Template_ResolvesNameCaseInsensitively() {
        var result = await catalog.CallAsync("add_target", """{"name":"M 31","ra":0.712,"dec":41.27,"template":"lrgb","rotation_degrees":0}""");

        Assert.That(result.IsError, Is.False, result.Text);
        Assert.That(catalog.Observatory.LastTarget!.TemplateName, Is.EqualTo("LRGB"));
        Assert.That(catalog.Observatory.LastTarget.Exposures, Is.Null);
    }

    [Test]
    public async Task AddTarget_UnknownTemplate_ListsAvailable() {
        var result = await catalog.CallAsync("add_target", """{"name":"M 31","ra":0.712,"dec":41.27,"template":"Broadband"}""");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.Text, Does.Contain("\"Narrowband Template\"").And.Contain("\"LRGB\""));
        Assert.That(catalog.Observatory.Calls, Does.Not.Contain("AddTarget"));
    }

    [Test]
    public async Task AddTarget_NoTemplatesSaved_SuggestsExposures() {
        catalog.Observatory.Templates.Clear();

        var result = await catalog.CallAsync("add_target", """{"name":"M 31","ra":0.712,"dec":41.27,"template":"LRGB"}""");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.Text, Does.Contain("pass 'exposures' instead"));
    }

    [TestCase("""{"name":"X","ra":1,"dec":1}""")]
    [TestCase("""{"name":"X","ra":1,"dec":1,"template":"LRGB","exposures":[{"exposure_seconds":1,"count":1}]}""")]
    public void AddTarget_TemplateXorExposures(string json) {
        Assert.That(catalog.ArgumentError("add_target", json), Does.Contain("exactly one of 'template'"));
    }

    [TestCase("slew_and_center", "false")]
    [TestCase("start_guiding", "true")]
    [TestCase("autofocus_at_start", "true")]
    [TestCase("dither_every", "2")]
    public void AddTarget_PlanOptionsWithTemplate_AreRejected(string option, string value) {
        var message = catalog.ArgumentError("add_target", $$"""{"name":"X","ra":1,"dec":1,"template":"LRGB","{{option}}":{{value}}}""");

        Assert.That(message, Does.Contain(option).And.Contain("only apply to an exposure plan"));
    }

    [TestCase("""{"ra":1,"dec":1,"exposures":[{"exposure_seconds":1,"count":1}]}""", "'name' is required")]
    [TestCase("""{"name":"","ra":1,"dec":1,"exposures":[{"exposure_seconds":1,"count":1}]}""", "'name' must not be empty")]
    [TestCase("""{"name":"X","dec":1,"exposures":[{"exposure_seconds":1,"count":1}]}""", "'ra' is required")]
    [TestCase("""{"name":"X","ra":"25h","dec":1,"exposures":[{"exposure_seconds":1,"count":1}]}""", "'ra'")]
    [TestCase("""{"name":"X","ra":1,"dec":"-91","exposures":[{"exposure_seconds":1,"count":1}]}""", "'dec'")]
    [TestCase("""{"name":"X","ra":1,"dec":1,"rotation_degrees":360,"exposures":[{"exposure_seconds":1,"count":1}]}""", "'rotation_degrees' must be at least 0 and below 360")]
    [TestCase("""{"name":"X","ra":1,"dec":1,"rotation_degrees":-1,"exposures":[{"exposure_seconds":1,"count":1}]}""", "'rotation_degrees'")]
    [TestCase("""{"name":"X","ra":1,"dec":1,"exposures":[]}""", "'exposures' must contain 1 to 20 entries")]
    [TestCase("""{"name":"X","ra":1,"dec":1,"exposures":{"exposure_seconds":1}}""", "'exposures' must be an array of objects")]
    [TestCase("""{"name":"X","ra":1,"dec":1,"exposures":[5]}""", "'exposures' must be an array of objects")]
    [TestCase("""{"name":"X","ra":1,"dec":1,"exposures":[{"count":1}]}""", "'exposures[0].exposure_seconds' is required")]
    [TestCase("""{"name":"X","ra":1,"dec":1,"exposures":[{"exposure_seconds":1}]}""", "'exposures[0].count' is required")]
    [TestCase("""{"name":"X","ra":1,"dec":1,"exposures":[{"exposure_seconds":1,"count":1},{"exposure_seconds":1,"count":0}]}""", "'exposures[1].count' must be at least 1 and at most 1000")]
    [TestCase("""{"name":"X","ra":1,"dec":1,"exposures":[{"exposure_seconds":1,"count":1001}]}""", "'exposures[0].count'")]
    [TestCase("""{"name":"X","ra":1,"dec":1,"exposures":[{"exposure_seconds":0,"count":1}]}""", "'exposures[0].exposure_seconds'")]
    [TestCase("""{"name":"X","ra":1,"dec":1,"exposures":[{"exposure_seconds":3601,"count":1}]}""", "'exposures[0].exposure_seconds'")]
    [TestCase("""{"name":"X","ra":1,"dec":1,"exposures":[{"exposure_seconds":1,"count":1,"binning":5}]}""", "'exposures[0].binning'")]
    [TestCase("""{"name":"X","ra":1,"dec":1,"exposures":[{"exposure_seconds":1,"count":1,"iso":800}]}""", "Unknown argument 'exposures[0].iso'")]
    [TestCase("""{"name":"X","ra":1,"dec":1,"exposures":[{"exposure_seconds":1,"count":2.5}]}""", "exposures[0]: Argument 'count' must be an integer")]
    [TestCase("""{"name":"X","ra":1,"dec":1,"exposures":[{"exposure_seconds":1,"count":1}],"dither_every":101}""", "'dither_every'")]
    [TestCase("""{"name":"X","ra":1,"dec":1,"exposures":[{"exposure_seconds":1,"count":1}],"dither_every":-1}""", "'dither_every'")]
    public void AddTarget_InvalidArguments(string json, string expected) {
        Assert.That(catalog.ArgumentError("add_target", json), Does.Contain(expected));
    }

    [Test]
    public void AddTarget_TooManyExposureBlocks() {
        var items = string.Join(",", Enumerable.Repeat("""{"exposure_seconds":1,"count":1}""", 21));

        Assert.That(catalog.ArgumentError("add_target", $$"""{"name":"X","ra":1,"dec":1,"exposures":[{{items}}]}"""), Does.Contain("1 to 20"));
    }

    [Test]
    public async Task AddTarget_ObservatoryFailure_IsError() {
        catalog.Observatory.SetResult("AddTarget", OperationResult.Fail("Sequencer not initialized."));

        var result = await catalog.CallAsync("add_target", """{"name":"X","ra":1,"dec":1,"exposures":[{"exposure_seconds":1,"count":1}]}""");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.Text, Is.EqualTo("Sequencer not initialized."));
    }

    // ----- save_sequence -----

    [TestCase("M42 Ha-OIII 2026-10-11", "M42 Ha-OIII 2026-10-11")]
    [TestCase("  night_1  ", "night_1")]
    [TestCase("v1.2", "v1.2")]
    [TestCase("Night.json", "Night")]
    [TestCase("Night.JSON", "Night")]
    [TestCase("Nébuleuse", "Nébuleuse")]
    public async Task SaveSequence_AcceptsSafeNames(string name, string expected) {
        var result = await catalog.CallAsync("save_sequence", $$"""{"name":"{{name}}"}""");

        Assert.That(result.IsError, Is.False, result.Text);
        Assert.That(catalog.Observatory.LastSave, Is.EqualTo((expected, false)));
    }

    [TestCase("../evil", "contains '/'")]
    [TestCase("a/b", "contains '/'")]
    [TestCase("a\\\\b", "contains '\\'")]
    [TestCase("C:night", "contains ':'")]
    [TestCase("night*", "contains '*'")]
    [TestCase("what?", "contains '?'")]
    [TestCase("<x>", "contains '<'")]
    [TestCase("a|b", "contains '|'")]
    [TestCase("tab\\tname", "contains")]
    [TestCase(".hidden", "must not start or end with '.'")]
    [TestCase("trailing.", "must not start or end with '.'")]
    [TestCase("..", "must not start or end with '.'")]
    [TestCase(".json", "must not be empty")]
    [TestCase("   ", "must not be empty")]
    public void SaveSequence_RejectsUnsafeNames(string name, string expected) {
        Assert.That(catalog.ArgumentError("save_sequence", $$"""{"name":"{{name}}"}"""), Does.Contain(expected));
    }

    [Test]
    public void SaveSequence_RejectsTooLongNames() {
        var name = new string('a', SaveSequenceTool.MaxNameLength + 1);

        Assert.That(catalog.ArgumentError("save_sequence", $$"""{"name":"{{name}}"}"""), Does.Contain("at most"));
    }

    [Test]
    public async Task SaveSequence_PassesOverwrite() {
        await catalog.CallAsync("save_sequence", """{"name":"Night","overwrite":true}""");

        Assert.That(catalog.Observatory.LastSave, Is.EqualTo(("Night", true)));
    }

    [Test]
    public async Task SaveSequence_ExistingFileFailureIsReported() {
        catalog.Observatory.SetResult("SaveSequence", OperationResult.Fail("A sequence named 'Night' exists. Pass overwrite: true to replace it."));

        var result = await catalog.CallAsync("save_sequence", """{"name":"Night"}""");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.Text, Does.Contain("overwrite"));
    }

    // ----- load / start / reset / clear -----

    [Test]
    public async Task LoadSequence_ResolvesSavedName() {
        var result = await catalog.CallAsync("load_sequence", """{"name":"orion night"}""");

        Assert.That(result.IsError, Is.False);
        Assert.That(catalog.Observatory.LastLoadedSequence, Is.EqualTo("Orion Night"));
    }

    [Test]
    public async Task LoadSequence_Unknown_ListsAvailable() {
        var result = await catalog.CallAsync("load_sequence", """{"name":"Andromeda"}""");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.Text, Does.Contain("\"Orion Night\""));
        Assert.That(catalog.Observatory.Calls, Does.Not.Contain("LoadSequence"));
    }

    [Test]
    public async Task StartSequence_PassesSkipValidation() {
        await catalog.CallAsync("start_sequence");

        Assert.That(catalog.Observatory.LastSkipValidation, Is.False);

        await catalog.CallAsync("start_sequence", """{"skip_validation":true}""");

        Assert.That(catalog.Observatory.LastSkipValidation, Is.True);
    }

    [Test]
    public async Task StartSequence_ValidationFailure_IsError() {
        catalog.Observatory.SetResult("StartSequence", OperationResult.Fail("Validation failed: camera not connected."));

        var result = await catalog.CallAsync("start_sequence");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.Text, Does.Contain("camera not connected"));
    }

    [TestCase("clear_targets", "ClearTargets")]
    [TestCase("reset_sequence", "ResetSequence")]
    public async Task SimpleSequenceTools_CallObservatory(string tool, string member) {
        var result = await catalog.CallAsync(tool);

        Assert.That(result.IsError, Is.False);
        Assert.That(catalog.Observatory.Calls, Does.Contain(member));
    }

    [Test]
    public async Task ListTools_ReturnCounts() {
        var templates = await catalog.CallAsync("list_target_templates");
        var sequences = await catalog.CallAsync("list_saved_sequences");

        Assert.That(templates.Structured!["count"]!.GetValue<int>(), Is.EqualTo(2));
        Assert.That(sequences.Structured!["sequences"]![0]!["fileName"]!.GetValue<string>(), Is.EqualTo("Orion Night.json"));
    }
}
