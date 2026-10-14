// SPDX-License-Identifier: MPL-2.0

using NINA.Mcp.Observatory;

namespace NINA.Mcp.Tools;

/// <summary>
/// Orders the matches of a catalog name search so that the object the user named comes first: an exact designation or
/// alias match, then names or aliases that start with the search text, then everything else that contains it. Case and
/// spaces are ignored ("m2" finds "M 2"). Within a tier the input order (the requested sort) is kept.
/// </summary>
public static class CatalogRanking {

    /// <summary>The database matches a name search by substring; this many are fetched before ranking and truncating.</summary>
    public const int NameSearchFetchLimit = 200;

    public static IReadOnlyList<CatalogObject> Rank(IEnumerable<CatalogObject> objects, string name) {
        ArgumentNullException.ThrowIfNull(objects);

        var query = Normalize(name);

        if (query.Length == 0) {
            return objects.ToList();
        }

        return objects
            .Select((item, index) => (Item: item, Index: index, Tier: Tier(item, query)))
            .OrderBy(entry => entry.Tier)
            .ThenBy(entry => entry.Index)
            .Select(entry => entry.Item)
            .ToList();
    }

    private static int Tier(CatalogObject item, string query) {
        var names = item.Aliases.Prepend(item.Name).Select(Normalize).ToList();

        if (names.Any(candidate => candidate == query)) {
            return 0;
        }

        return names.Any(candidate => candidate.StartsWith(query, StringComparison.Ordinal)) ? 1 : 2;
    }

    private static string Normalize(string? text) {
        return string.Concat((text ?? string.Empty).Where(character => !char.IsWhiteSpace(character))).ToUpperInvariant();
    }
}
