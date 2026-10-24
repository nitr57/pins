// SPDX-License-Identifier: MPL-2.0

using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Moq;
using NINA.Equipment.Equipment.MyGuider.Advanced;
using NINA.Profile.Interfaces;
using NUnit.Framework;
using NINA.GuideEngine.Coach;
using NINA.GuideEngine.Guiding;
using NINA.Equipment.Equipment.MyGuider.Internal;

namespace NINA.Test.Equipment.MyGuider.Internal;

[TestFixture]
public class CoachMappingTests
{
    /// <summary>
    /// Fills every property of an engine coach record with distinct values, maps it and compares the JSON of both
    /// sides: catches properties the mapping forgets (the DTOs and records share their property names).
    /// </summary>
    [Test]
    public void Status_mapping_keeps_every_property()
    {
        int counter = 0;
        var status = (CoachStatus)Fill(typeof(CoachStatus), 0, ref counter);
        var dto = CoachMapping.ToDto(status);

        AssertSameJson(dto, status);
    }

    [Test]
    public void Start_result_and_hint_mapping_keep_every_property()
    {
        int counter = 0;
        var result = (CoachStartResult)Fill(typeof(CoachStartResult), 0, ref counter);
        var hint = (CoachFinding)Fill(typeof(CoachFinding), 0, ref counter);

        AssertSameJson(CoachMapping.ToDto(result), result);
        AssertSameJson(CoachMapping.ToDto(hint), hint);
    }

    [Test]
    public void Parameters_keep_their_value_types()
    {
        var finding = new CoachFinding
        {
            Id = "drift.polarAlignment",
            Code = "drift.polarAlignment",
            Step = "Drift",
            Severity = "warning",
            Parameters = new() { ["arcmin"] = 7.3, ["decAssumed"] = false, ["mode"] = "North", ["periodSeconds"] = null },
        };

        var dto = CoachMapping.ToDto(finding);

        dto.Parameters["arcmin"].Should().Be(7.3);
        dto.Parameters["decAssumed"].Should().Be(false);
        dto.Parameters["mode"].Should().Be("North");
        dto.Parameters.Should().ContainKey("periodSeconds").WhoseValue.Should().BeNull();
    }

    [Test]
    public void Options_mapping_keeps_every_property()
    {
        var dto = new AdvancedCoachOptions
        {
            Steps = ["Drift", "Trials"],
            ExposureSeconds = [1.5, 3],
            Gains = [0, 120],
            FramesPerCombination = 7,
            DriftSeconds = 240,
            TrialSeconds = 90,
            RepeatBaseline = false,
            AllowCalibration = false,
        };

        var o = CoachMapping.FromDto(dto);

        AssertSameJson(o, dto);
        CoachMapping.FromDto(null).Should().BeEquivalentTo(new CoachOptions());
    }

    private static void AssertSameJson(object actual, object expected)
    {
        var a = JsonNode.Parse(JsonSerializer.Serialize(actual, actual.GetType()));
        var e = JsonNode.Parse(JsonSerializer.Serialize(expected, expected.GetType()));
        JsonNode.DeepEquals(a, e).Should().BeTrue($"mapped:\n{a}\nengine:\n{e}");
    }

    private static object Fill(Type t, int depth, ref int counter)
    {
        var u = Nullable.GetUnderlyingType(t) ?? t;
        if (u == typeof(double))
        {
            return ++counter + 0.25;
        }

        if (u == typeof(int))
        {
            return ++counter;
        }

        if (u == typeof(bool))
        {
            return true;
        }

        if (u == typeof(string))
        {
            return $"s{++counter}";
        }

        if (u == typeof(DateTime))
        {
            return new DateTime(2026, 9, 23, 20, 0, 0, DateTimeKind.Utc).AddSeconds(++counter);
        }

        if (u == typeof(Dictionary<string, object?>))
        {
            return new Dictionary<string, object?> { ["a"] = ++counter + 0.5, ["b"] = "text", ["c"] = true, ["d"] = null };
        }

        if (u.IsGenericType && u.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
        {
            var element = u.GetGenericArguments()[0];
            var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(element))!;
            for (int i = 0; i < 2 && depth < 4; i++)
            {
                list.Add(Fill(element, depth + 1, ref counter));
            }

            return list;
        }

        var ctor = u.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
        var args = new List<object?>();
        foreach (var p in ctor.GetParameters())
        {
            args.Add(Fill(p.ParameterType, depth + 1, ref counter));
        }

        object instance = ctor.Invoke(args.ToArray());
        if (ctor.GetParameters().Length == 0)
        {
            foreach (var p in u.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite))
            {
                // nested records only to a limited depth (the report inside the status, its camera check, ...)
                bool nested = !p.PropertyType.IsValueType && p.PropertyType != typeof(string)
                    && !(p.PropertyType.IsGenericType && p.PropertyType.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
                    && p.PropertyType != typeof(Dictionary<string, object?>);
                if (nested && depth >= 3)
                {
                    continue;
                }

                p.SetValue(instance, Fill(p.PropertyType, depth + 1, ref counter));
            }
        }

        return instance;
    }
}

[TestFixture]
public class GuidingCoachHostTests
{
    private string dir = string.Empty;

    [SetUp]
    public void SetUp() => dir = Path.Combine(Path.GetTempPath(), "pins-coach-" + Guid.NewGuid().ToString("N"));

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, true);
        }
    }

    private (GuidingCoachHost Host, List<(string Name, string Value)> Applied, Mock<IProfile> Profile) Create(double pixelSize, double focalLength)
    {
        var camera = new Mock<ICameraSettings>();
        camera.SetupGet(c => c.PixelSize).Returns(pixelSize);
        var telescope = new Mock<ITelescopeSettings>();
        telescope.SetupGet(t => t.FocalLength).Returns(focalLength);
        var profile = new Mock<IProfile>();
        profile.SetupGet(p => p.CameraSettings).Returns(camera.Object);
        profile.SetupGet(p => p.TelescopeSettings).Returns(telescope.Object);
        profile.SetupGet(p => p.GuiderSettings).Returns(new Mock<IGuiderSettings>().Object);
        profile.SetupGet(p => p.PluginSettings).Returns(new Mock<IPluginSettings>().Object);
        profile.SetupGet(p => p.Name).Returns("Rig A");
        var service = new Mock<IProfileService>();
        service.SetupGet(s => s.ActiveProfile).Returns(profile.Object);
        var options = new InternalGuiderOptions(service.Object, InternalGuiderOptions.SettingsId);
        var applied = new List<(string, string)>();
        bool Set(string name, string value, out string error)
        {
            error = string.Empty;
            if (value == "bad")
            {
                error = "invalid";
                return false;
            }

            applied.Add((name, value));
            return true;
        }

        var host = new GuidingCoachHost(service.Object, options, Set, () => new SettleParams(1.5, 10, 40), () => false, dir);
        return (host, applied, profile);
    }

    [Test]
    public void Imaging_scale_from_the_profile()
    {
        // 206264.8″ per radian; µm and mm
        Create(3.76, 800).Host.ImagingScale.Should().BeApproximately(0.9694446, 1e-7);
        Create(3.76, 0).Host.ImagingScale.Should().BeNull();
        Create(double.NaN, 800).Host.ImagingScale.Should().BeNull();
    }

    [Test]
    public void Apply_refuses_unknown_settings_without_applying_anything()
    {
        var (host, applied, _) = Create(3.76, 800);

        host.ApplySettings([new CoachSettingChange("RaAggression", "0.6"), new CoachSettingChange("NoSuchSetting", "1")], out var error)
            .Should().BeFalse();
        error.Should().Contain("NoSuchSetting");
        applied.Should().BeEmpty();

        host.ApplySettings([new CoachSettingChange("RaAggression", "0.6"), new CoachSettingChange("RaMinMove", "0.25")], out error)
            .Should().BeTrue();
        applied.Should().Equal(("RaAggression", "0.6"), ("RaMinMove", "0.25"));
    }

    [Test]
    public void Apply_reports_the_setting_that_failed()
    {
        var (host, _, _) = Create(3.76, 800);

        host.ApplySettings([new CoachSettingChange("Gain", "bad")], out var error).Should().BeFalse();
        error.Should().Be("Gain: invalid");
    }

    [Test]
    public void Current_values_come_from_the_plugin_settings()
    {
        var (host, _, _) = Create(3.76, 800);

        host.GetSettingValue("DecGuideMode").Should().Be("Auto");
        host.GetSettingValue("NoSuchSetting").Should().BeNull();
    }

    [Test]
    public void History_only_shows_reports_of_the_active_profile()
    {
        var (host, _, profile) = Create(3.76, 800);
        var t0 = new DateTime(2026, 9, 23, 21, 0, 0, DateTimeKind.Utc);
        host.SaveReport(new CoachReport { Id = "a", Timestamp = t0, ProfileName = "Rig A" });
        host.SaveReport(new CoachReport { Id = "b", Timestamp = t0.AddHours(1), ProfileName = "Rig B" });
        host.SaveReport(new CoachReport { Id = "c", Timestamp = t0.AddHours(2), ProfileName = "Rig A" });

        host.LoadReports(10).Select(r => r.Id).Should().Equal("c", "a");

        profile.SetupGet(p => p.Name).Returns("Rig B");
        host.LoadReports(10).Select(r => r.Id).Should().Equal("b");
    }

    [Test]
    public void Saving_a_report_again_replaces_it()
    {
        var (host, _, _) = Create(3.76, 800);
        var t0 = new DateTime(2026, 9, 23, 21, 0, 0, DateTimeKind.Utc);
        host.SaveReport(new CoachReport { Id = "a", Timestamp = t0, ProfileName = "Rig A", Grade = "fair" });
        host.SaveReport(new CoachReport { Id = "a", Timestamp = t0, ProfileName = "Rig A", Grade = "good" });

        host.LoadReports(10).Should().ContainSingle().Which.Grade.Should().Be("good");
    }
}
