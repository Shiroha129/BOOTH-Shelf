using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;

namespace BoothShelf;

internal static class FreeOwnershipSelfTest
{
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static JsonObject Variant(int id, string? name, int price) => new()
    {
        ["id"] = id, ["name"] = name, ["price"] = price, ["type"] = "digital",
        ["downloadables"] = new JsonArray(new JsonObject { ["name"] = id + ".zip", ["file_size"] = "1 MB", ["created_at"] = "2026-01-01T00:00:00Z", ["updated_at"] = "2026-01-01T00:00:00Z" })
    };
    private static JsonObject Product(int id, string name, params JsonObject[] variants) => new()
    { ["id"] = id, ["name"] = name, ["variations"] = new JsonArray(variants.Select(value => (JsonNode)value).ToArray()) };
    private static JsonObject Row(int id, string title, string section = "free") => new()
    { ["id"] = id.ToString(), ["title"] = title, ["section"] = section, ["ownedVariationIds"] = new JsonArray() };
    private sealed class Handler(Dictionary<int, JsonObject> data) : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Calls++;
            Check(request.RequestUri!.Host == "api.booth.pm" && !request.Headers.Contains("Cookie"), "版本识别额外访问账号或下载接口");
            var id = int.Parse(request.RequestUri.Segments.Last());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(data[id].ToJsonString()) });
        }
    }

    internal static async Task<JsonObject> RunAsync(string root)
    {
        var data = new Dictionary<int, JsonObject>
        {
            [1] = Product(1, "Costume", Variant(11, "Full", 1000), Variant(12, "Hair", 700), Variant(13, "Other free", 0)),
            [2] = Product(2, "Free tool", Variant(21, null, 9999999)),
            [3] = Product(3, "Mixed pack", Variant(31, "Trial", 0), Variant(32, "Paid", 1000)),
            [4] = Product(4, "Multiple", Variant(41, "A", 0), Variant(42, "B", 0), Variant(43, "C", 0))
        };
        var rows = new[] { Row(1, "Costume (Hair)"), Row(2, "Free tool"), Row(3, "Mixed pack (Trial)"), Row(4, "Multiple (A)") };
        rows[3]["libraryLabels"] = new JsonArray("Multiple (A)", "Multiple (B)");
        Check(LibraryOwnership.FreeVersions(data[1], rows[0]).SequenceEqual(new[] { "12" }), "曾经免费后来改价的版本无法识别");
        Check(LibraryOwnership.FreeVersions(data[2], rows[1]).SequenceEqual(new[] { "21" }), "没有版本名称的免费记录无法识别");
        Check(LibraryOwnership.FreeVersions(data[3], rows[2]).SequenceEqual(new[] { "31" }), "误选未获取的付费版本");
        Check(LibraryOwnership.FreeVersions(data[4], rows[3]).SequenceEqual(new[] { "41", "42" }), "同商品多条免费记录丢失");
        Check(LibraryOwnership.FreeVersions(data[1], Row(1, "Costume (Hair)", "bought")).Length == 0, "名称推断扩散到已购库");
        Check(LibraryOwnership.FreeVersions(data[4], Row(4, "Multiple")).Length == 0, "省略名称时猜测了多个版本");
        var ambiguous = Product(5, "Ambiguous", Variant(51, "Same", 0), Variant(52, "Same", 100));
        Check(LibraryOwnership.FreeVersions(ambiguous, Row(5, "Ambiguous (Same)")).Length == 0, "重名版本未拒绝歧义");
        var normalized = Product(6, " Asset  name ", Variant(61, "試用（A）", 0));
        Check(LibraryOwnership.FreeVersions(normalized, Row(6, "Asset name (試用(A))")).SequenceEqual(new[] { "61" }), "空白或全角括号规范化错误");

        var owned = UpdateMonitor.FileVersions(data[1], "1", ["11"]);
        File.WriteAllText(Path.Combine(root, "update-monitor.jsonl"), new JsonObject
        { ["id"] = "1", ["owned"] = new JsonArray("11"), ["versions"] = owned, ["status"] = "ownership" }.ToJsonString() + "\n");
        var time = DateTimeOffset.Parse("2026-11-10T00:00:00Z");
        var handler = new Handler(data);
        using var monitor = new UpdateMonitor(root, handler, () => time);
        var snapshot = new JsonObject { ["items"] = new JsonArray(rows.Select(row => (JsonNode)row.DeepClone()).ToArray()) };
        snapshot["items"]![0]!["title"] = "Costume (Full)";
        snapshot["items"]![0]!["section"] = "bought";
        monitor.SetLibrary(snapshot);
        Task<JsonObject> Read(string url, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var free = new Uri(url).AbsolutePath.EndsWith("free_downloads");
            return Task.FromResult(new JsonObject
            {
                ["sourceUrl"] = url, ["sort"] = "file_updated_at_desc", ["searchPage"] = 1, ["page"] = 1, ["maxPage"] = 1,
                ["serverDate"] = time.ToString("R"), ["unfiltered"] = true, ["empty"] = !free,
                ["items"] = free ? new JsonArray(rows.Select(row => (JsonNode)row.DeepClone()).ToArray()) : new JsonArray()
            });
        }
        await monitor.CheckBatchAsync(Read, CancellationToken.None);
        Check(monitor.State()["sourcesReady"]!.GetValue<int>() == 3 && monitor.State()["unavailable"]!.GetValue<int>() == 0
            && monitor.State()["items"]!.AsArray().Count == 0 && handler.Calls == 4, "免费库未恢复正常或额外扫描历史页");
        var record = File.ReadLines(Path.Combine(root, "update-monitor.jsonl")).Select(line => JsonNode.Parse(line)!).Last(value => value["id"]!.ToString() == "1");
        Check(record["owned"]!.AsArray().Select(value => value!.ToString()).SequenceEqual(new[] { "11", "12" }), "旧版已确认的付费版本未保留或免费版本未添加");
        time = time.AddMinutes(16);
        data[1]["variations"]![0]!["downloadables"]![0]!["updated_at"] = time.ToString("O");
        data[1]["variations"]![1]!["downloadables"]![0]!["updated_at"] = time.ToString("O");
        data[1]["variations"]![2]!["downloadables"]![0]!["updated_at"] = time.ToString("O");
        data[3]["variations"]![1]!["downloadables"]![0]!["updated_at"] = time.ToString("O");
        await monitor.CheckBatchAsync(Read, CancellationToken.None);
        var notices = monitor.State()["items"]!.AsArray();
        Check(notices.Count == 1 && notices[0]!["files"]!.AsArray().Select(file => file!.ToString()).SequenceEqual(new[] { "11.zip", "12.zip" }),
            "已确认版本更新漏报或其他免费/付费版本误报");
        time = time.AddMinutes(16);
        rows[3]["libraryLabels"] = new JsonArray("Multiple (Unknown)");
        await monitor.CheckBatchAsync(Read, CancellationToken.None);
        var source = monitor.State()["sources"]!.AsArray().First(value => value!["id"]!.ToString() == "free")!;
        Check(source["error"]!.GetValue<bool>() && source["ownership"]!.GetValue<int>() == 1, "无法识别版本时未显示来源和具体原因");
        return new JsonObject { ["freePageWithoutDownloadMenu"] = true, ["formerlyFreeNowPaid"] = true, ["unnamedVariant"] = true,
            ["multipleLabels"] = true, ["ambiguousNamesRejected"] = true, ["legacyOwnedPreserved"] = true,
            ["unrelatedVariantsExcluded"] = true, ["allSourcesReady"] = true, ["sourceErrorDetails"] = true };
    }
}
