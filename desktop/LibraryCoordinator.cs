using System.Text.Json.Nodes;

namespace BoothShelf;

internal static class LibraryCoordinator
{
    internal static readonly (string Id, string Label, string Url)[] Sections =
    [
        ("bought", "已购", ShelfWindow.LibraryUrl),
        ("gift", "礼物", ShelfWindow.LibraryUrl + "/gifts"),
        ("free", "免费", ShelfWindow.LibraryUrl + "/free_downloads")
    ];

    internal static async Task<JsonObject> SynchronizeAsync(string dataRoot, JsonObject? baseline, string mode,
        Func<string, int, CancellationToken, Task<LibraryPage>> read, Func<JsonArray, CancellationToken, Task> enrich,
        Action<string, int, int, int, string, double?> progress, CancellationToken token,
        Func<int, CancellationToken, Task>? delay = null)
    {
        if (mode is not ("full" or "quick")) throw new InvalidDataException("同步模式无效");
        var sections = new JsonObject();
        foreach (var section in Sections)
        {
            token.ThrowIfCancellationRequested();
            var previous = Baseline(baseline, section.Id);
            var newest = mode == "quick" ? previous["items"]!.AsArray().FirstOrDefault()?["id"]?.GetValue<string>() : null;
            var known = previous["items"]!.AsArray().Select(item => item!["id"]!.GetValue<string>()).ToHashSet();
            async Task<LibraryPage> ReadPage(int number, CancellationToken pageToken)
            {
                var page = await read(section.Url, number, pageToken);
                page.Validate(number);
                if (mode == "full") await enrich(page.Items, pageToken);
                else
                {
                    var added = new JsonArray(page.Items.OfType<JsonObject>().TakeWhile(item => item["id"]!.GetValue<string>() != newest)
                        .Where(item => !known.Contains(item["id"]!.GetValue<string>())).Select(item => (JsonNode)item.DeepClone()).ToArray());
                    if (added.Count > 0) await enrich(added, pageToken);
                    var enriched = added.OfType<JsonObject>().GroupBy(item => item["id"]!.GetValue<string>()).ToDictionary(group => group.Key, group => group.First());
                    foreach (var item in page.Items.OfType<JsonObject>())
                        if (enriched.TryGetValue(item["id"]!.GetValue<string>(), out var metadata))
                            foreach (var field in metadata.Where(field => field.Key != "files")) item[field.Key] = field.Value?.DeepClone();
                }
                return page;
            }
            progress(section.Label, 0, 1, 0, "读取商品与标签", null);
            var first = await ReadPage(1, token);
            var cacheRoot = Path.Combine(dataRoot, "sections", section.Id);
            Directory.CreateDirectory(cacheRoot);
            var cache = SyncCache.Open(cacheRoot, first, mode, previous["syncedAt"]?.GetValue<string>() ?? "");
            var engine = new LibrarySync(ReadPage, (page, total, count, status, seconds) =>
                progress(section.Label, page, total, count, status, seconds), delay);
            var result = await engine.RunAsync(cache, first, previous, token);
            foreach (var item in result["items"]!.AsArray().OfType<JsonObject>())
            {
                item.Remove("updatedRank");
                item.Remove("updatedRanks");
            }
            foreach (var page in result["pages"]!.AsArray()) page!["url"] = section.Url + "?page=" + page["page"]!.GetValue<int>();
            sections[section.Id] = result;
        }
        return sections;
    }

    internal static JsonObject Baseline(JsonObject? snapshot, string section)
    {
        if (snapshot?["sections"]?[section] is JsonObject stored)
        {
            var result = (JsonObject)stored.DeepClone();
            var combined = (snapshot["items"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToDictionary(item => item["id"]!.GetValue<string>());
            foreach (var item in result["items"]!.AsArray().OfType<JsonObject>())
                if (combined.TryGetValue(item["id"]!.GetValue<string>(), out var asset))
                    foreach (var field in new[] { "purchaseJpy", "purchaseCount", "purchasedAt" })
                        if (asset[field] != null) item[field] = asset[field]!.DeepClone();
            return result;
        }
        return new JsonObject
        {
            ["items"] = new JsonArray((snapshot?["items"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                .Where(item => item["sections"] is JsonArray sources ? sources.Any(source => source?.GetValue<string>() == section)
                    : (item["section"]?.GetValue<string>() ?? "bought") == section)
                .OrderBy(item => item["acquiredRanks"]?[section]?.GetValue<int>() ?? 0)
                .Select(item => (JsonNode)item.DeepClone()).ToArray()),
            ["syncedAt"] = snapshot?["syncedAt"]?.DeepClone()
        };
    }

    internal static JsonObject Combine(JsonObject sections, string mode, bool complete)
    {
        var combined = new Dictionary<string, JsonObject>();
        foreach (var section in Sections)
        {
            var rank = 0;
            foreach (var value in (sections[section.Id]?["items"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            {
                var id = value["id"]!.GetValue<string>();
                if (!combined.TryGetValue(id, out var item))
                {
                    combined[id] = item = (JsonObject)value.DeepClone();
                    item["sections"] = new JsonArray();
                    item["acquiredRanks"] = new JsonObject();
                    item.Remove("updatedRanks");
                    item.Remove("updatedRank");
                    item["libraryUrls"] = new JsonArray();
                }
                item["sections"]!.AsArray().Add(section.Id);
                item["acquiredRanks"]![section.Id] = rank++;
                item["libraryUrls"]!.AsArray().Add(value["libraryUrl"]?.DeepClone());
                LibraryOwnership.Merge(item, value);
                var files = (item["files"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Select(file => file["id"]!.GetValue<string>()).ToHashSet();
                item["files"] ??= new JsonArray();
                foreach (var file in (value["files"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                    if (files.Add(file["id"]!.GetValue<string>())) item["files"]!.AsArray().Add(file.DeepClone());
            }
        }
        return new JsonObject { ["schemaVersion"] = 2, ["items"] = new JsonArray(combined.Values.Select(value => (JsonNode)value).ToArray()),
            ["sections"] = sections.DeepClone(), ["complete"] = complete, ["syncKind"] = mode,
            ["syncedAt"] = complete ? DateTimeOffset.UtcNow.ToString("O") : null,
            ["totalPages"] = sections.Sum(section => section.Value?["totalPages"]?.GetValue<int>() ?? 0) };
    }
}
