using System.Text.Json.Nodes;

namespace BoothShelf;

internal static class SyncWorkflowSelfTest
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static JsonObject Asset(string id) => new()
    {
        ["id"] = id, ["title"] = "Asset " + id, ["tags"] = new JsonArray(),
        ["files"] = new JsonArray(new JsonObject { ["id"] = id, ["name"] = id + ".zip" })
    };

    private static LibraryPage Page(string url, int number, int total, params string[] ids) => new()
    {
        Page = number, DisplayedPage = number, MaxPage = total, Empty = ids.Length == 0,
        Next = number < total ? url + "?page=" + (number + 1) : null,
        Items = new JsonArray(ids.Select(id =>
        {
            var item = Asset(id);
            item["libraryUrl"] = url + "?page=" + number;
            return (JsonNode)item;
        }).ToArray())
    };

    internal static async Task<JsonObject> RunAsync(string root)
    {
        var sources = new JsonObject();
        foreach (var section in LibraryCoordinator.Sections)
        {
            var ids = section.Id switch { "bought" => new[] { "100", "50" }, "gift" => new[] { "200", "150" }, _ => new[] { "300", "250" } };
            sources[section.Id] = new JsonObject { ["items"] = new JsonArray(ids.Select(id => (JsonNode)Asset(id)).ToArray()), ["syncedAt"] = "baseline" };
        }
        sources["bought"]!["items"]![1]!["tags"] = new JsonArray("保留的标签");
        sources["bought"]!["items"]![1]!["files"]![0]!["id"] = "1500";
        var baseline = LibraryCoordinator.Combine(sources, "full", true);
        baseline["items"]![1]!["purchaseJpy"] = 500m;
        var original = baseline.ToJsonString();
        var requests = new List<string>();
        var enriched = new List<string>();
        Task<LibraryPage> Read(string url, int number, CancellationToken token)
        {
            requests.Add(url + "?page=" + number);
            var section = LibraryCoordinator.Sections.First(value => value.Url == url).Id;
            var ids = (section, number) switch
            {
                ("bought", 1) => new[] { "101", "50" },
                ("bought", 2) => new[] { "102", "100", "999" },
                ("bought", 3) => new[] { "9" },
                ("gift", 1) => new[] { "100" },
                ("gift", 2) => new[] { "201", "200" },
                ("gift", 3) => new[] { "19" },
                ("free", 1) => new[] { "301", "300", "302" },
                ("free", 2) => new[] { "29" },
                ("free", 3) => new[] { "39" },
                _ => throw new InvalidOperationException("请求越过末页或错误来源")
            };
            return Task.FromResult(Page(url, number, 3, ids));
        }
        Task Enrich(JsonArray items, CancellationToken token)
        {
            foreach (var item in items)
            {
                enriched.Add(item!["id"]!.GetValue<string>());
                item["tags"] = new JsonArray("同步标签");
            }
            return Task.CompletedTask;
        }
        Task<JsonObject> Run(string name, JsonObject? previous, string mode,
            Func<string, int, CancellationToken, Task<LibraryPage>>? reader = null) => LibraryCoordinator.SynchronizeAsync(
                Path.Combine(root, name), previous, mode, reader ?? Read, Enrich, (_, _, _, _, _, _) => { }, CancellationToken.None, (_, _) => Task.CompletedTask);
        var quick = await Run("quick", baseline, "quick");
        Check(requests.Count == 5 && LibraryCoordinator.Sections.All(section => requests.Contains(section.Url + "?page=1")), "快速更新漏掉来源或越过停止点预取");
        Check(quick["bought"]!["items"]!.AsArray().Select(item => item!["id"]!.GetValue<string>()).SequenceEqual(new[] { "101", "50", "102", "100" }), "遇到普通旧资产错误停止，或收录了边界后商品");
        Check(quick["gift"]!["items"]!.AsArray().Count == 4 && quick["free"]!["items"]!.AsArray().Count == 3, "不同来源错误共用最新资产停止点");
        var old = quick["bought"]!["items"]![1]!;
        Check(old["tags"]![0]!.GetValue<string>() == "保留的标签" && old["purchaseJpy"]!.GetValue<decimal>() == 500 && old["files"]!.AsArray().Count == 2, "快速更新丢失旧标签、价格或变体");
        Check(enriched.SequenceEqual(new[] { "101", "102", "100", "201", "301" }), "标签请求包含旧库、停止点或边界后商品");
        Check(baseline.ToJsonString() == original, "同步修改了原始基线");
        Check(quick["gift"]!["pages"]![1]!["url"]!.GetValue<string>().Contains("/gifts?page=2"), "礼物页被记录为已购地址");

        requests.Clear();
        enriched.Clear();
        await Run("full", baseline, "full");
        Check(requests.Count == 12 && LibraryCoordinator.Sections.All(section => requests.Count(url => url.StartsWith(section.Url + "?")) == 4), "全局更新没有完整读取三来源，或额外执行第二轮扫描");

        requests.Clear();
        enriched.Clear();
        await Run("unchanged", baseline, "quick", (url, number, _) =>
        {
            requests.Add(url);
            var section = LibraryCoordinator.Sections.First(value => value.Url == url).Id;
            return Task.FromResult(Page(url, number, 696, sources[section]!["items"]![0]!["id"]!.GetValue<string>(), "9999"));
        });
        Check(requests.Count == 3 && enriched.Count == 0, "无新增仍扫描多页或补全旧商品标签");

        requests.Clear();
        enriched.Clear();
        var empty = await Run("no-baseline", null, "quick");
        Check(requests.Count == 9 && empty["free"]!["items"]!.AsArray().Count == 5, "无基线时未完整建立三来源");
        requests.Clear();
        var absent = new JsonObject { ["items"] = new JsonArray(Asset("888")) };
        var missing = await Run("missing-marker", absent, "quick");
        Check(requests.Count == 9 && missing["bought"]!["items"]!.AsArray().Any(item => item!["id"]!.GetValue<string>() == "888"), "最新资产已下架时丢失旧资产或未扫描到末页");

        requests.Clear();
        var legacy = new JsonObject { ["items"] = new JsonArray(Asset("101"), Asset("50")) };
        var migrated = await Run("legacy", legacy, "quick");
        Check(requests.Count == 7 && migrated["bought"]!["items"]!.AsArray().Count == 2
            && migrated["gift"]!["items"]!.AsArray().Count == 4, "旧版资料库被强制全量已购扫描或没有初始化礼物、免费");
        var ranked = new JsonObject { ["items"] = new JsonArray(
            new JsonObject { ["id"] = "1", ["sections"] = new JsonArray("gift"), ["acquiredRanks"] = new JsonObject { ["gift"] = 1 } },
            new JsonObject { ["id"] = "2", ["sections"] = new JsonArray("gift"), ["acquiredRanks"] = new JsonObject { ["gift"] = 0 } }) };
        Check(LibraryCoordinator.Baseline(ranked, "gift")["items"]![0]!["id"]!.GetValue<string>() == "2", "缺少独立来源快照时未按该来源获取顺序找最新项");
        await Run("empty-sections", baseline, "quick", (url, number, _) => Task.FromResult(Page(url, number, 1)));
        return new JsonObject { ["quickRequestsAcrossThreeSections"] = 5, ["fullRequestsAcrossThreeSections"] = 12,
            ["independentMarkers"] = true, ["noSpeculativePages"] = true, ["newItemsOnlyMetadata"] = true,
            ["preservesTagsPricesVariants"] = true, ["firstPageStops"] = true, ["initialAndLegacySync"] = true,
            ["missingMarkerPreservesLibrary"] = true, ["emptySections"] = true };
    }
}
