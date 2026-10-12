// SPDX-License-Identifier: MPL-2.0

using NINA.Mcp.Protocol;
using NINA.Mcp.Test.Fakes;
using NINA.Mcp.Tools;
using NUnit.Framework;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NINA.Mcp.Test;

[TestFixture]
public class ToolsCatalogTests {

    private static readonly string[] ExpectedOrder = [
        "get_status",
        "get_recent_images",
        "get_sequence",
        "list_saved_sequences",
        "list_target_templates",
        "get_job",
        "list_jobs",
        "stop_all",
        "cancel_job",
        "stop_sequence",
        "stop_guiding",
        "connect_device",
        "disconnect_device",
        "slew_mount",
        "park_mount",
        "unpark_mount",
        "home_mount",
        "set_tracking",
        "capture_image",
        "cool_camera",
        "warm_camera",
        "move_focuser",
        "run_autofocus",
        "change_filter",
        "start_guiding",
        "set_switch",
        "load_sequence",
        "add_target",
        "clear_targets",
        "save_sequence",
        "start_sequence",
        "reset_sequence"
    ];

    private static readonly string[] QueryTools = [
        "get_status",
        "get_recent_images",
        "get_sequence",
        "list_saved_sequences",
        "list_target_templates",
        "get_job",
        "list_jobs"
    ];

    private static readonly string[] StopTools = ["stop_all", "cancel_job", "stop_sequence", "stop_guiding"];

    private static readonly string[] ChangeTools = ["add_target"];

    private static readonly string[] DestructiveChangeTools = ["load_sequence", "clear_targets", "reset_sequence", "save_sequence"];

    private static readonly string[] JobTools = [
        "connect_device",
        "disconnect_device",
        "slew_mount",
        "park_mount",
        "unpark_mount",
        "home_mount",
        "capture_image",
        "cool_camera",
        "warm_camera",
        "move_focuser",
        "run_autofocus",
        "change_filter",
        "start_guiding",
        "stop_guiding",
        "set_switch"
    ];

    private ToolsTestCatalog catalog = null!;

    [SetUp]
    public void SetUp() {
        catalog = new ToolsTestCatalog();
    }

    [TearDown]
    public void TearDown() {
        catalog.Dispose();
    }

    public static IEnumerable<string> AllToolNames() {
        return ExpectedOrder;
    }

    [Test]
    public void RegistersAllV1ToolsInListingOrder() {
        Assert.That(catalog.Tools.Select(tool => tool.Name), Is.EqualTo(ExpectedOrder));
    }

    [Test]
    public void ToolNames_AreUniqueSnakeCase() {
        var names = catalog.Tools.Select(tool => tool.Name).ToList();

        Assert.That(names, Is.Unique);

        foreach (var name in names) {
            Assert.That(Regex.IsMatch(name, "^[a-z][a-z0-9]*(_[a-z0-9]+)*$"), Is.True, name);
        }
    }

    [Test]
    public void Tools_ShareTheObservatoryToolBase() {
        Assert.That(catalog.Tools.All(tool => tool is ObservatoryTool), Is.True);
    }

    [TestCaseSource(nameof(AllToolNames))]
    public void Tool_HasTitleAndSubstantialDescription(string name) {
        var tool = catalog.Tool(name);

        Assert.That(tool.Title, Is.Not.Empty);
        Assert.That(tool.Description.Length, Is.GreaterThan(80), "Descriptions should explain units, side effects and use.");
        Assert.That(tool.Description, Does.Not.Contain("  "));
    }

    [TestCaseSource(nameof(AllToolNames))]
    public void Tool_SchemaIsStrictObject(string name) {
        var schema = catalog.Tool(name).InputSchema;

        Assert.That(schema["type"]!.GetValue<string>(), Is.EqualTo("object"));
        Assert.That(schema["additionalProperties"]!.GetValue<bool>(), Is.False);
        Assert.That(schema["properties"], Is.InstanceOf<JsonObject>());

        AssertSchemaSane(schema, name);
    }

    [TestCaseSource(nameof(AllToolNames))]
    public void Tool_SchemaIsStableAndSerializable(string name) {
        var tool = catalog.Tool(name);

        Assert.That(tool.InputSchema, Is.SameAs(tool.InputSchema));

        var described = McpDispatcher.DescribeTool(tool);

        Assert.That(described["inputSchema"]!.ToJsonString(), Is.EqualTo(tool.InputSchema.ToJsonString()));
    }

    [TestCaseSource(nameof(AllToolNames))]
    public void Tool_AnnotationsMatchItsKind(string name) {
        var annotations = catalog.Tool(name).Annotations;

        // By reference: ChangeDestructive and Hardware are equal records, only the instance tells the kind apart.
        if (QueryTools.Contains(name)) {
            Assert.That(annotations, Is.SameAs(ToolAnnotations.Query));
        } else if (StopTools.Contains(name)) {
            Assert.That(annotations, Is.SameAs(ToolAnnotations.Stop));
        } else if (ChangeTools.Contains(name)) {
            Assert.That(annotations, Is.SameAs(ToolAnnotations.Change));
        } else if (DestructiveChangeTools.Contains(name)) {
            Assert.That(annotations, Is.SameAs(ToolAnnotations.ChangeDestructive));
        } else {
            Assert.That(annotations, Is.SameAs(ToolAnnotations.Hardware));
        }
    }

    [TestCaseSource(nameof(AllToolNames))]
    public void Tool_ReadOnlyModeAvailability(string name) {
        var tool = catalog.Tool(name);
        var expected = QueryTools.Contains(name) || name is "stop_all" or "cancel_job";

        Assert.That(tool.AllowedInReadOnlyMode, Is.EqualTo(expected));
    }

    [TestCaseSource(nameof(AllToolNames))]
    public void Tool_JobToolsHaveWaitSeconds(string name) {
        var properties = catalog.Tool(name).InputSchema["properties"]!.AsObject();
        var expected = JobTools.Contains(name) || name is "get_job" or "cancel_job";

        Assert.That(properties.ContainsKey("wait_seconds"), Is.EqualTo(expected));

        if (expected) {
            Assert.That(properties["wait_seconds"]!["minimum"]!.GetValue<double>(), Is.EqualTo(0));
            Assert.That(properties["wait_seconds"]!["maximum"]!.GetValue<double>(), Is.EqualTo(55));
        }
    }

    [TestCaseSource(nameof(AllToolNames))]
    public void Tool_ForcePropertyExactlyOnForceableTools(string name) {
        var tool = (ObservatoryTool)catalog.Tool(name);
        var properties = tool.InputSchema["properties"]!.AsObject();

        Assert.That(properties.ContainsKey("force"), Is.EqualTo(tool.Guard == SequenceGuard.Forceable));
    }

    [Test]
    public void Guards_MatchTheSpec() {
        string[] strict = ["load_sequence", "clear_targets", "start_sequence", "reset_sequence"];
        string[] none = [.. QueryTools, "stop_all", "cancel_job", "stop_sequence", "save_sequence"];

        foreach (var tool in catalog.Tools.Cast<ObservatoryTool>()) {
            var expected = strict.Contains(tool.Name)
                ? SequenceGuard.Strict
                : none.Contains(tool.Name) ? SequenceGuard.None : SequenceGuard.Forceable;

            Assert.That(tool.Guard, Is.EqualTo(expected), tool.Name);
        }
    }

    [Test]
    public void DeviceEnum_ListsSnakeCaseKinds() {
        var device = catalog.Tool("connect_device").InputSchema["properties"]!["device"]!;
        var values = device["enum"]!.AsArray().Select(value => value!.GetValue<string>()).ToList();

        Assert.That(values, Is.EqualTo(new[] {
            "camera", "mount", "focuser", "filter_wheel", "guider", "rotator", "flat_device", "dome", "switch", "weather", "safety_monitor"
        }));
    }

    [Test]
    public void CoordinateArguments_AcceptNumberOrString() {
        var slew = catalog.Tool("slew_mount").InputSchema["properties"]!;

        var ra = slew["ra"]!["anyOf"]!.AsArray();
        var dec = slew["dec"]!["anyOf"]!.AsArray();

        Assert.That(slew["ra"]!["type"], Is.Null, "no type arrays: several MCP clients reject them");
        Assert.That(ra.Select(option => option!["type"]!.GetValue<string>()), Is.EqualTo(new[] { "number", "string" }));
        Assert.That(ra[0]!["exclusiveMaximum"]!.GetValue<double>(), Is.EqualTo(24));
        Assert.That(dec[0]!["minimum"]!.GetValue<double>(), Is.EqualTo(-90));
        Assert.That(dec[0]!["maximum"]!.GetValue<double>(), Is.EqualTo(90));
        Assert.That(dec[1]!["maxLength"]!.GetValue<int>(), Is.EqualTo(40));
    }

    [Test]
    public void AddTarget_ExposureItemsSchemaIsStrict() {
        var exposures = catalog.Tool("add_target").InputSchema["properties"]!["exposures"]!;

        Assert.That(exposures["type"]!.GetValue<string>(), Is.EqualTo("array"));
        Assert.That(exposures["minItems"]!.GetValue<int>(), Is.EqualTo(1));

        var items = exposures["items"]!.AsObject();

        Assert.That(items["additionalProperties"]!.GetValue<bool>(), Is.False);
        Assert.That(items["required"]!.AsArray().Select(value => value!.GetValue<string>()), Is.EquivalentTo(new[] { "exposure_seconds", "count" }));
        Assert.That(items["properties"]!["count"]!["maximum"]!.GetValue<double>(), Is.EqualTo(1000));
    }

    [Test]
    public void Instructions_CoverTheGuide() {
        var text = catalog.Instructions.Text;

        Assert.That(text, Does.Contain("J2000"));
        Assert.That(text, Does.Contain("get_job"));
        Assert.That(text, Does.Contain("force"));
        Assert.That(text, Does.Contain("stop_all"));
        Assert.That(text, Does.Contain("start_sequence"));
        Assert.That(text, Does.Contain("get_recent_images"));
        Assert.That(text.Length, Is.LessThan(4000));
    }

    [Test]
    public void Instructions_MentionOnlyRegisteredTools() {
        var names = catalog.Tools.Select(tool => tool.Name).ToHashSet();
        var mentioned = Regex.Matches(catalog.Instructions.Text, "\\b[a-z]+(?:_[a-z]+)+\\b").Select(match => match.Value).Distinct();

        foreach (var word in mentioned) {
            var known = names.Contains(word) || word is "wait_seconds" or "filter_wheel";

            Assert.That(known, Is.True, $"Unknown tool '{word}' in instructions.");
        }
    }

    [Test]
    public void Descriptions_MentionOnlyRegisteredToolsOrArguments() {
        var names = catalog.Tools.Select(tool => tool.Name).ToHashSet();

        foreach (var tool in catalog.Tools) {
            var properties = tool.InputSchema["properties"]!.AsObject().Select(property => property.Key).ToHashSet();
            var mentioned = Regex.Matches(tool.Description, "\\b[a-z]+(?:_[a-z]+)+\\b").Select(match => match.Value).Distinct();

            foreach (var word in mentioned) {
                var known = names.Contains(word)
                    || properties.Contains(word)
                    || DeviceKinds.All.Contains(word)
                    || word is "camera_cooler" or "wait_seconds" or "force_calibration" or "slew_and_center";

                Assert.That(known, Is.True, $"{tool.Name} mentions unknown '{word}'.");
            }
        }
    }

    private static void AssertSchemaSane(JsonObject schema, string context) {
        var properties = schema["properties"]!.AsObject();

        if (schema["required"] is JsonArray required) {
            var names = required.Select(value => value!.GetValue<string>()).ToList();

            Assert.That(names, Is.Unique, context);

            foreach (var name in names) {
                Assert.That(properties.ContainsKey(name), Is.True, $"{context}: required '{name}' is not a property.");
            }
        }

        foreach (var (name, node) in properties) {
            var property = node!.AsObject();
            var where = $"{context}.{name}";

            Assert.That(Regex.IsMatch(name, "^[a-z][a-z0-9]*(_[a-z0-9]+)*$"), Is.True, where);
            Assert.That(property["description"]?.GetValue<string>(), Is.Not.Null.And.Not.Empty, where);
            Assert.That(property.ContainsKey("type") || property["anyOf"] is JsonArray, Is.True, where);

            if (property["anyOf"] is JsonArray alternatives) {
                foreach (var alternative in alternatives) {
                    Assert.That(alternative!["type"]!.GetValueKind(), Is.EqualTo(System.Text.Json.JsonValueKind.String), where);
                }

                continue;
            }

            Assert.That(property["type"]!.GetValueKind(), Is.EqualTo(System.Text.Json.JsonValueKind.String), $"{where}: no type arrays");

            var minimum = Number(property["minimum"]);
            var maximum = Number(property["maximum"]) ?? Number(property["exclusiveMaximum"]);
            var defaultNumber = Number(property["default"]);

            if (minimum is not null && maximum is not null) {
                Assert.That(minimum, Is.LessThanOrEqualTo(maximum), where);
            }

            if (defaultNumber is not null && minimum is not null) {
                Assert.That(defaultNumber, Is.GreaterThanOrEqualTo(minimum), where);
            }

            if (defaultNumber is not null && maximum is not null) {
                Assert.That(defaultNumber, Is.LessThanOrEqualTo(maximum), where);
            }

            if (property["type"]!.ToJsonString() == "\"array\"") {
                AssertSchemaSane(property["items"]!.AsObject(), where);
                Assert.That(property["items"]!["additionalProperties"]!.GetValue<bool>(), Is.False, where);
            }
        }
    }

    /// <summary>A JSON number node as double (integers and doubles); null for absent or non-numeric nodes.</summary>
    private static double? Number(JsonNode? node) {
        if (node is not JsonValue value || value.GetValueKind() != System.Text.Json.JsonValueKind.Number) {
            return null;
        }

        return double.Parse(value.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
