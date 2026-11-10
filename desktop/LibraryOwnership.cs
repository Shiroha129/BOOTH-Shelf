using System.Text.Json.Nodes;
using System.Text;
using System.Text.RegularExpressions;

namespace BoothShelf;

internal static class LibraryOwnership
{
    internal static string[] Versions(JsonObject item) => (item["ownedVariationIds"] as JsonArray ?? new JsonArray())
        .Select(value => value?.ToString() ?? "").Where(value => Regex.IsMatch(value, @"^\d{1,20}$")).Distinct().Order().ToArray();
    internal static void Merge(JsonObject target, JsonObject source)
    {
        target["ownedVariationIds"] = new JsonArray(Versions(target).Union(Versions(source)).Order().Select(value => (JsonNode)JsonValue.Create(value)!).ToArray());
        target["libraryLabels"] = new JsonArray(Labels(target).Union(Labels(source)).Select(value => (JsonNode)JsonValue.Create(value)!).ToArray());
    }

    internal static string[] Labels(JsonObject item) => (item["libraryLabels"] as JsonArray ?? new JsonArray(item["title"]?.DeepClone()))
        .Select(value => value?.ToString() ?? "").Where(value => !string.IsNullOrWhiteSpace(value)).Distinct().ToArray();

    internal static string[] FreeVersions(JsonObject data, JsonObject row)
    {
        if (row["section"]?.ToString() != "free" || data["id"]?.ToString() != row["id"]?.ToString() || data["variations"] is not JsonArray variants) return [];
        var title = Normalize(data["name"]?.ToString());
        if (title.Length == 0) return [];
        var result = new HashSet<string>();
        foreach (var label in Labels(row))
        {
            var matched = variants.OfType<JsonObject>().Where(variant =>
            {
                var name = Normalize(variant["name"]?.ToString());
                return variant["type"]?.ToString() == "digital" && Regex.IsMatch(variant["id"]?.ToString() ?? "", @"^\d{1,20}$")
                    && Normalize(label) == (name.Length == 0 ? title : title + " (" + name + ")");
            }).ToArray();
            if (matched.Length != 1) return [];
            result.Add(matched[0]["id"]!.ToString());
        }
        return result.Order().ToArray();
    }

    private static string Normalize(string? value) => Regex.Replace((value ?? "").Normalize(NormalizationForm.FormKC), @"\s+", " ").Trim();
}
