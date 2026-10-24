// SPDX-License-Identifier: MPL-2.0

using NINA.Mcp.Observatory;
using NINA.Mcp.Tools;
using NUnit.Framework;

namespace NINA.Mcp.Test;

/// <summary>find_targets ranks the named object first before truncating.</summary>
[TestFixture]
public class CatalogRankingTests {

    [Test]
    public void M2_RanksBeforeTheTenMessierObjectsM20ToM29() {
        // A substring search for "M 2" sorted by display name: the M 20 to M 29 objects come before NGC 7089 (M 2).
        var found = new[] {
            Object("IC 4715", "M 24"),
            Object("IC 4725", "M 25"),
            Object("NGC 6494", "M 23"),
            Object("NGC 6514", "M 20"),
            Object("NGC 6531", "M 21"),
            Object("NGC 6626", "M 28"),
            Object("NGC 6656", "M 22"),
            Object("NGC 6694", "M 26"),
            Object("NGC 6853", "M 27"),
            Object("NGC 6913", "M 29"),
            Object("NGC 7089", "M 2")
        };

        var ranked = CatalogRanking.Rank(found, "M 2");

        Assert.That(ranked[0].Name, Is.EqualTo("NGC 7089"));
        Assert.That(ranked.Take(10).Select(item => item.Name), Does.Contain("NGC 7089"));
        Assert.That(ranked.Skip(1).Select(item => item.Name), Is.EqualTo(found.Take(10).Select(item => item.Name)), "the rest keeps its order");
    }

    [Test]
    public void Matching_IgnoresCaseAndSpaces() {
        var found = new[] { Object("NGC 6514", "M 20"), Object("NGC 7089", "M 2") };

        Assert.That(CatalogRanking.Rank(found, "m2")[0].Name, Is.EqualTo("NGC 7089"));
        Assert.That(CatalogRanking.Rank(found, " ngc7089 ")[0].Name, Is.EqualTo("NGC 7089"));
    }

    [Test]
    public void Prefix_RanksBeforeSubstring() {
        var found = new[] { Object("Little Orion", null), Object("Orion Nebula", "M 42"), Object("NGC 1976", "Orion") };

        var ranked = CatalogRanking.Rank(found, "orion");

        Assert.That(ranked.Select(item => item.Name), Is.EqualTo(new[] { "NGC 1976", "Orion Nebula", "Little Orion" }));
    }

    [Test]
    public void EmptyName_KeepsTheOrder() {
        var found = new[] { Object("B", null), Object("A", null) };

        Assert.That(CatalogRanking.Rank(found, " ").Select(item => item.Name), Is.EqualTo(new[] { "B", "A" }));
    }

    private static CatalogObject Object(string name, string? alias) {
        return new CatalogObject(name, alias is null ? [] : [alias], "GLOCL", null, 0, 0, null, null, null, null, null);
    }
}
