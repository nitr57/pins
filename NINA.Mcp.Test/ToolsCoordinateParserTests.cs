// SPDX-License-Identifier: MPL-2.0

using NINA.Mcp.Tools;
using NUnit.Framework;
using System.Text.Json;

namespace NINA.Mcp.Test;

[TestFixture]
public class ToolsCoordinateParserTests {

    private const double Tolerance = 1e-9;

    // ----- Right ascension: valid -----

    [TestCase("05h35m17.3s", 5 + (35 / 60.0) + (17.3 / 3600))]
    [TestCase("5h35m17.3s", 5 + (35 / 60.0) + (17.3 / 3600))]
    [TestCase("05h 35m 17.3s", 5 + (35 / 60.0) + (17.3 / 3600))]
    [TestCase("05 h 35 m 17.3 s", 5 + (35 / 60.0) + (17.3 / 3600))]
    [TestCase("05H35M17.3S", 5 + (35 / 60.0) + (17.3 / 3600))]
    [TestCase("5:35:17.3", 5 + (35 / 60.0) + (17.3 / 3600))]
    [TestCase("05:35:17", 5 + (35 / 60.0) + (17 / 3600.0))]
    [TestCase("5 35 17.3", 5 + (35 / 60.0) + (17.3 / 3600))]
    [TestCase("  5   35   17.3  ", 5 + (35 / 60.0) + (17.3 / 3600))]
    [TestCase("5\t35\t17.3", 5 + (35 / 60.0) + (17.3 / 3600))]
    [TestCase("5:35", 5 + (35 / 60.0))]
    [TestCase("5h35m", 5 + (35 / 60.0))]
    [TestCase("5h35.5m", 5 + (35.5 / 60))]
    [TestCase("5:35.5", 5 + (35.5 / 60))]
    [TestCase("5h", 5.0)]
    [TestCase("5.5883", 5.5883)]
    [TestCase("+5.5", 5.5)]
    [TestCase("0", 0.0)]
    [TestCase("00:00:00", 0.0)]
    [TestCase("0h0m0s", 0.0)]
    [TestCase("23:59:59.99", 23 + (59 / 60.0) + (59.99 / 3600))]
    [TestCase("23h59m59.9s", 23 + (59 / 60.0) + (59.9 / 3600))]
    [TestCase("12h30m00s", 12.5)]
    [TestCase("12h 30′ 00″", 12.5)]
    [TestCase("12 30'00\"", 12.5)]
    [TestCase("00h42m44.3s", (42 / 60.0) + (44.3 / 3600))]
    public void RightAscension_Valid(string text, double expected) {
        var ok = CoordinateParser.TryParseRightAscension(text, out var hours, out var error);

        Assert.That(ok, Is.True, error);
        Assert.That(hours, Is.EqualTo(expected).Within(Tolerance));
    }

    // ----- Right ascension: invalid -----

    [TestCase("24:00:00")]
    [TestCase("24h")]
    [TestCase("24")]
    [TestCase("25:00:00")]
    [TestCase("23:60:00")]
    [TestCase("23:59:60")]
    [TestCase("-1:00:00")]
    [TestCase("-00:30:00")]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("abc")]
    [TestCase("5:35:17:3")]
    [TestCase("5:35:")]
    [TestCase("5::35")]
    [TestCase("5.5:30")]
    [TestCase("5:30.5:10")]
    [TestCase("83.8°")]
    [TestCase("83d49m")]
    [TestCase("5m35h")]
    [TestCase("5h35s")]
    [TestCase("5h35m17.3sx")]
    [TestCase("5.")]
    [TestCase("1.2.3")]
    [TestCase("--5")]
    [TestCase("5 35 17 3")]
    public void RightAscension_Invalid(string text) {
        var ok = CoordinateParser.TryParseRightAscension(text, out _, out var error);

        Assert.That(ok, Is.False);
        Assert.That(error, Is.Not.Empty);
    }

    [Test]
    public void RightAscension_Null_IsInvalid() {
        Assert.That(CoordinateParser.TryParseRightAscension(null, out _, out _), Is.False);
    }

    [Test]
    public void RightAscension_DegreesMarker_ExplainsHoursVsDegrees() {
        CoordinateParser.TryParseRightAscension("83.8°", out _, out var error);

        Assert.That(error, Does.Contain("divide degrees by 15"));
    }

    // ----- Declination: valid -----

    [TestCase("-05°23'28\"", -(5 + (23 / 60.0) + (28 / 3600.0)))]
    [TestCase("-05°23′28″", -(5 + (23 / 60.0) + (28 / 3600.0)))]
    [TestCase("−05°23′28″", -(5 + (23 / 60.0) + (28 / 3600.0)))]
    [TestCase("-05° 23' 28\"", -(5 + (23 / 60.0) + (28 / 3600.0)))]
    [TestCase("-05°23'28''", -(5 + (23 / 60.0) + (28 / 3600.0)))]
    [TestCase("-5:23:28", -(5 + (23 / 60.0) + (28 / 3600.0)))]
    [TestCase("-5 23 28", -(5 + (23 / 60.0) + (28 / 3600.0)))]
    [TestCase("- 5 23 28", -(5 + (23 / 60.0) + (28 / 3600.0)))]
    [TestCase("-5d23m28s", -(5 + (23 / 60.0) + (28 / 3600.0)))]
    [TestCase("-5D23M28S", -(5 + (23 / 60.0) + (28 / 3600.0)))]
    [TestCase("-5deg23m28s", -(5 + (23 / 60.0) + (28 / 3600.0)))]
    [TestCase("-5deg 23' 28\"", -(5 + (23 / 60.0) + (28 / 3600.0)))]
    [TestCase("+41°16′09″", 41 + (16 / 60.0) + (9 / 3600.0))]
    [TestCase("+41:16:09", 41 + (16 / 60.0) + (9 / 3600.0))]
    [TestCase("41 16 9", 41 + (16 / 60.0) + (9 / 3600.0))]
    [TestCase("41°16.15'", 41 + (16.15 / 60))]
    [TestCase("41:16", 41 + (16 / 60.0))]
    [TestCase("41°", 41.0)]
    [TestCase("-5.391", -5.391)]
    [TestCase("+5.391", 5.391)]
    [TestCase("90", 90.0)]
    [TestCase("+90:00:00", 90.0)]
    [TestCase("-90:00:00", -90.0)]
    [TestCase("-90°", -90.0)]
    [TestCase("0", 0.0)]
    [TestCase("-00:30:00", -0.5)]
    [TestCase("-0:30:00", -0.5)]
    [TestCase("-00°30'00\"", -0.5)]
    [TestCase("-0 30 0", -0.5)]
    [TestCase("-00:00:36", -0.01)]
    [TestCase("-0.5", -0.5)]
    [TestCase("+89:59:59.9", 89 + (59 / 60.0) + (59.9 / 3600))]
    [TestCase("-05º23'28\"", -(5 + (23 / 60.0) + (28 / 3600.0)))]
    [TestCase("-05˚23’28”", -(5 + (23 / 60.0) + (28 / 3600.0)))]
    public void Declination_Valid(string text, double expected) {
        var ok = CoordinateParser.TryParseDeclination(text, out var degrees, out var error);

        Assert.That(ok, Is.True, error);
        Assert.That(degrees, Is.EqualTo(expected).Within(Tolerance));
    }

    [Test]
    public void Declination_NegativeZeroDegrees_StaysNegative() {
        CoordinateParser.TryParseDeclination("-00:30:00", out var degrees, out _);

        Assert.That(degrees, Is.LessThan(0));
        Assert.That(degrees, Is.EqualTo(-0.5).Within(Tolerance));
    }

    // ----- Declination: invalid -----

    [TestCase("90:00:01")]
    [TestCase("-90:00:01")]
    [TestCase("91")]
    [TestCase("-90.0001")]
    [TestCase("45:60:00")]
    [TestCase("45:00:60")]
    [TestCase("5h23m")]
    [TestCase("")]
    [TestCase("north")]
    [TestCase("-")]
    [TestCase("+-5")]
    [TestCase("5:23:28:1")]
    [TestCase("5.5°30'")]
    [TestCase("5'23°")]
    [TestCase("-5°23\"")]
    [TestCase("5° 23' 28\" N")]
    public void Declination_Invalid(string text) {
        var ok = CoordinateParser.TryParseDeclination(text, out _, out var error);

        Assert.That(ok, Is.False);
        Assert.That(error, Is.Not.Empty);
    }

    // ----- Argument reading (number or string) -----

    [TestCase("""{"ra":5.5}""", 5.5)]
    [TestCase("""{"ra":0}""", 0.0)]
    [TestCase("""{"ra":23.999}""", 23.999)]
    [TestCase("""{"ra":"5:30:00"}""", 5.5)]
    [TestCase("""{"ra":"5.5"}""", 5.5)]
    public void ReadRightAscension_NumberOrString(string json, double expected) {
        var value = CoordinateParser.ReadRightAscension(Arguments(json), "ra");

        Assert.That(value, Is.EqualTo(expected).Within(Tolerance));
    }

    [TestCase("""{"ra":24}""")]
    [TestCase("""{"ra":-0.1}""")]
    [TestCase("""{"ra":83.8}""")]
    [TestCase("""{"ra":"24:00:00"}""")]
    [TestCase("""{"ra":true}""")]
    [TestCase("""{"ra":[5]}""")]
    [TestCase("""{"ra":{"h":5}}""")]
    public void ReadRightAscension_Invalid_Throws(string json) {
        var exception = Assert.Throws<ToolArgumentException>(() => CoordinateParser.ReadRightAscension(Arguments(json), "ra"));

        Assert.That(exception!.Message, Does.Contain("'ra'"));
    }

    [Test]
    public void ReadRightAscension_DegreesNumber_Hints() {
        var exception = Assert.Throws<ToolArgumentException>(() => CoordinateParser.ReadRightAscension(Arguments("""{"ra":83.8}"""), "ra"));

        Assert.That(exception!.Message, Does.Contain("divide degrees by 15"));
    }

    [TestCase("""{"dec":-5.391}""", -5.391)]
    [TestCase("""{"dec":90}""", 90.0)]
    [TestCase("""{"dec":-90}""", -90.0)]
    [TestCase("""{"dec":"-00:30:00"}""", -0.5)]
    [TestCase("""{"dec":"+41°16′09″"}""", 41 + (16 / 60.0) + (9 / 3600.0))]
    public void ReadDeclination_NumberOrString(string json, double expected) {
        var value = CoordinateParser.ReadDeclination(Arguments(json), "dec");

        Assert.That(value, Is.EqualTo(expected).Within(Tolerance));
    }

    [TestCase("""{"dec":90.5}""")]
    [TestCase("""{"dec":-91}""")]
    [TestCase("""{"dec":"95:00:00"}""")]
    [TestCase("""{"dec":false}""")]
    public void ReadDeclination_Invalid_Throws(string json) {
        var exception = Assert.Throws<ToolArgumentException>(() => CoordinateParser.ReadDeclination(Arguments(json), "dec"));

        Assert.That(exception!.Message, Does.Contain("'dec'"));
    }

    [Test]
    public void Read_Absent_ReturnsNull() {
        Assert.That(CoordinateParser.ReadRightAscension(Arguments("{}"), "ra"), Is.Null);
        Assert.That(CoordinateParser.ReadDeclination(Arguments("""{"dec":null}"""), "dec"), Is.Null);
    }

    [Test]
    public void Read_UsesDisplayNameInMessages() {
        var exception = Assert.Throws<ToolArgumentException>(
            () => CoordinateParser.ReadDeclination(Arguments("""{"dec":"x"}"""), "dec", "targets[2].dec"));

        Assert.That(exception!.Message, Does.Contain("'targets[2].dec'"));
    }

    // ----- Formatting -----

    [TestCase(5 + (35 / 60.0) + (17.3 / 3600), "05h35m17.3s")]
    [TestCase(0.0, "00h00m00.0s")]
    [TestCase(23.99999999, "00h00m00.0s")]
    [TestCase(12.5, "12h30m00.0s")]
    public void FormatRightAscension(double hours, string expected) {
        Assert.That(CoordinateParser.FormatRightAscension(hours), Is.EqualTo(expected));
    }

    [TestCase(-(5 + (23 / 60.0) + (28 / 3600.0)), "-05°23'28\"")]
    [TestCase(-0.5, "-00°30'00\"")]
    [TestCase(41.26916667, "+41°16'09\"")]
    [TestCase(0.0, "+00°00'00\"")]
    [TestCase(-90.0, "-90°00'00\"")]
    public void FormatDeclination(double degrees, string expected) {
        Assert.That(CoordinateParser.FormatDeclination(degrees), Is.EqualTo(expected));
    }

    [TestCase("05h35m17.3s")]
    [TestCase("23h59m59.9s")]
    public void RightAscension_FormatParseRoundTrip(string text) {
        CoordinateParser.TryParseRightAscension(text, out var hours, out _);

        Assert.That(CoordinateParser.FormatRightAscension(hours), Is.EqualTo(text));
    }

    [TestCase("-05°23'28\"")]
    [TestCase("-00°30'00\"")]
    [TestCase("+89°59'59\"")]
    public void Declination_FormatParseRoundTrip(string text) {
        CoordinateParser.TryParseDeclination(text, out var degrees, out _);

        Assert.That(CoordinateParser.FormatDeclination(degrees), Is.EqualTo(text));
    }

    private static ToolArguments Arguments(string json) {
        return new ToolArguments(JsonDocument.Parse(json).RootElement.Clone());
    }
}
