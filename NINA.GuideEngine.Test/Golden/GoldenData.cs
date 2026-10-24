// SPDX-License-Identifier: MPL-2.0

using System.Globalization;
using System.Text.Json;
using NUnit.Framework;

namespace NINA.GuideEngine.Test.Golden;

/// <summary>Access to golden results generated from compiled PHD2 sources (tools/phd2-algo-golden).</summary>
internal static class GoldenData
{
    private static readonly Lazy<JsonDocument> Doc = new(() =>
    {
        string path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Golden", "phd2-golden.json");
        return JsonDocument.Parse(File.ReadAllText(path));
    });

    public static JsonElement Root => Doc.Value.RootElement;

    public static IEnumerable<JsonElement> Section(string name) => Root.GetProperty(name).EnumerateArray();

    public static JsonElement Case(string section, string name) =>
        Section(section).First(c => c.GetProperty("case").GetString() == name);

    public static IEnumerable<string> CaseNames(string section)
    {
        // TestCaseSource is evaluated before TestContext has a test directory; resolve from the assembly.
        string path = Path.Combine(Path.GetDirectoryName(typeof(GoldenData).Assembly.Location)!, "Golden", "phd2-golden.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.GetProperty(section).EnumerateArray().Select(c => c.GetProperty("case").GetString()!).ToList();
    }

    public static double D(this JsonElement e, string prop) => Num(e.GetProperty(prop));

    public static double Num(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Number => v.GetDouble(),
        JsonValueKind.String => double.Parse(v.GetString()!, CultureInfo.InvariantCulture),
        _ => throw new InvalidOperationException($"not a number: {v}"),
    };

    public static int I(this JsonElement e, string prop) => e.GetProperty(prop).GetInt32();

    public static string S(this JsonElement e, string prop) => e.GetProperty(prop).GetString()!;

    public static bool B(this JsonElement e, string prop) => e.GetProperty(prop).GetBoolean();
}
