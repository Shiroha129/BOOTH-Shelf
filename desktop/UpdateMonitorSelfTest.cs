using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace BoothShelf;

internal static class UpdateMonitorSelfTest
{
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static JsonObject File(string name, string date = "2026-11-01T00:00:00Z") => new()
    { ["name"] = name, ["updated_at"] = date, ["created_at"] = "2026-10-01T00:00:00Z", ["file_size"] = "2 MB" };
    private static JsonObject Product(int id) => new()
    {
        ["id"] = id, ["variations"] = new JsonArray(
            new JsonObject { ["id"] = id * 10 + 1, ["downloadables"] = new JsonArray(File("owned.zip")) },
            new JsonObject { ["id"] = id * 10 + 2, ["downloadables"] = new JsonArray(File("not-owned.zip")) })
    };
    private static JsonObject Item(int id, params string[] versions) => new()
    {
        ["id"] = id.ToString(), ["title"] = "Asset " + id, ["section"] = "bought",
        ["ownedVariationIds"] = new JsonArray(versions.Select(value => (JsonNode)JsonValue.Create(value)!).ToArray())
    };
    private sealed class Handler : HttpMessageHandler
    {
        internal readonly Dictionary<int, JsonObject> Data = new();
        internal readonly Dictionary<int, HttpStatusCode> Failures = new();
        internal int Calls;
        internal int Conditional;
        internal int Active;
        internal int Maximum;
        internal JsonObject Get(int id)
        {
            if (!Data.TryGetValue(id, out var data)) Data[id] = data = Product(id);
            return data;
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Check(request.RequestUri!.Host == "api.booth.pm" && request.RequestUri.AbsolutePath.StartsWith("/vroid/items/")
                && !request.Headers.Contains("Cookie") && request.Headers.Authorization == null, "通知请求发送了凭据或访问下载接口");
            string body;
            HttpStatusCode status;
            string etag;
            lock (Data)
            {
                Calls++;
                Active++;
                Maximum = Math.Max(Maximum, Active);
                var id = int.Parse(request.RequestUri.Segments.Last());
                body = Get(id).ToJsonString();
                etag = "\"" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))) + "\"";
                status = Failures.GetValueOrDefault(id, HttpStatusCode.OK);
                if (request.Headers.IfNoneMatch.Any(value => value.ToString() == etag) && status == HttpStatusCode.OK)
                { status = HttpStatusCode.NotModified; Conditional++; }
            }
            try { await Task.Delay(5, token); }
            finally { lock (Data) Active--; }
            var response = new HttpResponseMessage(status) { Content = new StringContent(body) };
            response.Headers.ETag = new EntityTagHeaderValue(etag);
            if ((int)status == 429) response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(20));
            return response;
        }
    }
    private sealed class Fixture
    {
        internal DateTimeOffset Time = DateTimeOffset.Parse("2026-11-10T01:00:00Z");
        internal Handler Handler = new();
        internal readonly List<string> Reads = new();
        internal readonly Dictionary<string, int[][]> Rows = new()
        { ["/library"] = [[1, 2, 3, 4, 5, 6]], ["/library/gifts"] = [[41, 42, 43, 44, 45, 46]], ["/library/free_downloads"] = [[81, 82, 83, 84, 85, 86]] };
        internal readonly JsonObject Library = new()
        { ["items"] = new JsonArray(Enumerable.Range(1, 7067).Select(id => (JsonNode)Item(id, (id * 10 + 1).ToString())).ToArray()) };
        internal Func<JsonObject, JsonObject>? Transform;
        internal bool RateLimitPage;
        internal Task<JsonObject> Read(string url, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (RateLimitPage) throw new SyncReadException("429", true, 1200000);
            Reads.Add(url);
            var uri = new Uri(url);
            Check(uri.Host == "accounts.booth.pm" && uri.Query.Contains("sort=file_updated_at_desc"), "没有使用官方文件更新时间排序");
            var number = int.Parse(uri.Query.Split("page=").Last());
            Check(number <= Rows[uri.AbsolutePath].Length, "越过增量边界读取了历史页");
            var rows = Rows[uri.AbsolutePath][number - 1];
            var page = new JsonObject
            {
                ["sourceUrl"] = url, ["sort"] = "file_updated_at_desc", ["searchPage"] = number, ["page"] = number,
                ["unfiltered"] = true, ["serverDate"] = Time.ToString("R"), ["maxPage"] = rows.Length == 0 ? 1 : 600,
                ["empty"] = rows.Length == 0, ["next"] = rows.Length == 0 ? null : uri.GetLeftPart(UriPartial.Path) + "?sort=file_updated_at_desc&page=" + (number + 1),
                ["items"] = new JsonArray(rows.Select(id =>
                {
                    var item = Item(id, (id * 10 + 1).ToString());
                    item["section"] = uri.AbsolutePath.EndsWith("free_downloads") ? "free" : uri.AbsolutePath.EndsWith("gifts") ? "gift" : "bought";
                    return (JsonNode)item;
                }).ToArray())
            };
            return Task.FromResult(Transform?.Invoke(page) ?? page);
        }
        internal void Update(int id, DateTimeOffset time, int variant = 0) => Handler.Get(id)["variations"]![variant]!["downloadables"]![0]!["updated_at"] = time.ToString("O");
        internal async Task Round(UpdateMonitor monitor)
        {
            Time = Time.AddMinutes(16);
            await monitor.CheckBatchAsync(Read, CancellationToken.None);
        }
    }
    internal static async Task<JsonObject> RunAsync(string root)
    {
        var fixture = new Fixture();
        var notices = 0;
        string readId;
        using (var monitor = new UpdateMonitor(root, fixture.Handler, () => fixture.Time))
        {
            monitor.SetLibrary(fixture.Library);
            monitor.NewNotifications += count => notices += count;
            await monitor.CheckBatchAsync(fixture.Read, CancellationToken.None);
            Check(notices == 0 && fixture.Handler.Calls == 18 && fixture.Reads.Count == 3 && monitor.State()["sourcesReady"]!.GetValue<int>() == 3,
                "7067 件资产首次检查未限制到三来源头部，或误报历史更新");
            Check(fixture.Handler.Maximum == 4, "文件元数据未使用四路有界并发");
            Check(!await monitor.CheckBatchAsync(fixture.Read, CancellationToken.None), "空闲时持续发送请求");
            monitor.RequestCheck();
            Check(!await monitor.CheckBatchAsync(fixture.Read, CancellationToken.None), "手动按钮连点绕过最短间隔");
            await fixture.Round(monitor);
            Check(fixture.Handler.Conditional == 12 && notices == 0, "未使用304条件请求");
            var changed = fixture.Time.AddMinutes(2);
            foreach (var id in Enumerable.Range(100, 8).Concat([41, 81])) fixture.Update(id, changed);
            fixture.Rows["/library"] = [[100, 101, 102, 103, 104, 105], [106, 107, 1, 2, 3, 4]];
            fixture.Reads.Clear();
            await fixture.Round(monitor);
            Check(notices == 10 && fixture.Reads.Count == 4, "跨页更新或已购礼物免费更新漏报");
            fixture.Update(100, fixture.Time.AddMinutes(2));
            await fixture.Round(monitor);
            Check(notices == 11, "商品仍处于顶部且ID顺序未变时漏报");
            await fixture.Round(monitor);
            Check(notices == 11, "重复检查重复通知");
            fixture.Update(100, fixture.Time.AddMinutes(1), 1);
            fixture.Handler.Get(100)["name"] = "description only";
            await fixture.Round(monitor);
            Check(notices == 11, "未拥有版本或文案变动误报");
            fixture.Handler.Get(100)["variations"]![0]!["downloadables"]!.AsArray().Add(File("new.zip", fixture.Time.AddMinutes(1).ToString("O")));
            await fixture.Round(monitor);
            Check(notices == 12, "新增文件未通知");
            var owned = fixture.Library["items"]![99]!["ownedVariationIds"]!.AsArray();
            owned.Add("1002");
            monitor.SetLibrary(fixture.Library);
            await fixture.Round(monitor);
            Check(notices == 12, "新获得版本被误报为文件更新");
            fixture.Update(100, fixture.Time.AddMinutes(1), 1);
            await fixture.Round(monitor);
            Check(notices == 13, "新拥有版本后续更新漏报");
            fixture.Update(100, fixture.Time.AddMinutes(1));
            fixture.Handler.Failures[100] = HttpStatusCode.Forbidden;
            var watermark = JsonNode.Parse(System.IO.File.ReadAllText(Path.Combine(root, "update-monitor-state.json")))!["sections"]!["bought"]!["through"]!.ToString();
            await fixture.Round(monitor);
            Check(monitor.State()["issue"]!.GetValue<bool>() && notices == 13, "请求失败被当成成功");
            Check(JsonNode.Parse(System.IO.File.ReadAllText(Path.Combine(root, "update-monitor-state.json")))!["sections"]!["bought"]!["through"]!.ToString() == watermark,
                "失败推进了检查时间，可能漏报");
            fixture.Handler.Failures.Clear();
            await fixture.Round(monitor);
            Check(notices == 14 && !monitor.State()["issue"]!.GetValue<bool>(), "失败恢复后未补报");
            fixture.Handler.Get(100)["variations"]![0]!["downloadables"] = new JsonArray();
            await fixture.Round(monitor);
            Check(notices == 14 && monitor.State()["unavailable"]!.GetValue<int>() == 1, "异常空文件覆盖了基线");
            fixture.Handler.Get(100)["variations"]![0]!["downloadables"] = new JsonArray(File("owned.zip", fixture.Time.AddMinutes(1).ToString("O")));
            fixture.Handler.Failures[100] = (HttpStatusCode)429;
            var calls = fixture.Handler.Calls;
            await fixture.Round(monitor);
            Check(fixture.Handler.Calls - calls == 4 && monitor.State()["retryAt"] != null, "限流后仍继续发出后续批次请求");
            monitor.RequestCheck();
            Check(!await monitor.CheckBatchAsync(fixture.Read, CancellationToken.None), "手动检查绕过限流");
            readId = monitor.State()["items"]![0]!["id"]!.ToString();
            monitor.MarkRead(readId);
        }
        using (var restored = new UpdateMonitor(root, new Handler(), () => fixture.Time))
        {
            restored.SetLibrary(fixture.Library);
            Check(restored.State()["items"]!.AsArray().Count == 14 && restored.State()["items"]![0]!["read"]!.GetValue<bool>(), "升级或重启丢失通知与已读状态");
            restored.RequestCheck();
            Check(!await restored.CheckBatchAsync(fixture.Read, CancellationToken.None), "重启绕过限流等待");
        }
        await EdgeCasesAsync(Path.Combine(root, "edges"));
        await BoundaryCasesAsync(Path.Combine(root, "boundaries"));
        return new JsonObject { ["baselineItems"] = 7067, ["baselinePages"] = 3, ["baselineMetadataRequests"] = 18,
            ["boundedConcurrency"] = 4, ["threeSources"] = true, ["sameTopItemUpdate"] = true, ["unseenOldItemUpdate"] = true,
            ["crossPageUpdates"] = true, ["etag304"] = true, ["ownedVersionsOnly"] = true, ["failureDoesNotAdvance"] = true,
            ["rateLimitPersists"] = true, ["notificationsPersist"] = true, ["edgeCases"] = true, ["sameTimestampAndCancellation"] = true };
    }

    private static async Task EdgeCasesAsync(string root)
    {
        Directory.CreateDirectory(root);
        var fixture = new Fixture();
        using var monitor = new UpdateMonitor(root, fixture.Handler, () => fixture.Time.AddDays(2));
        fixture.Library["items"]![99]!["ownedVariationIds"] = new JsonArray();
        monitor.SetLibrary(fixture.Library);
        await monitor.CheckBatchAsync(fixture.Read, CancellationToken.None);
        fixture.Rows["/library"] = [[100, 9999, 1, 2, 3, 4]];
        fixture.Update(100, fixture.Time.AddMinutes(2));
        fixture.Update(9999, fixture.Time.AddMinutes(2));
        await fixture.Round(monitor);
        Check(monitor.State()["items"]!.AsArray().Count == 1 && monitor.State()["items"]![0]!["itemId"]!.ToString() == "100",
            "旧库版本识别失败、未同步商品误报或客户端时钟偏移导致漏报");
        fixture.Library["items"]!.AsArray().Add(Item(9999, "99991"));
        monitor.SetLibrary(fixture.Library);
        await fixture.Round(monitor);
        Check(monitor.State()["items"]!.AsArray().Count == 1, "新购买资产历史更新误报");
        fixture.Transform = page => { page["sort"] = "library_added_at_desc"; return page; };
        await fixture.Round(monitor);
        Check(monitor.State()["issue"]!.GetValue<bool>(), "未确认网页排序仍推进检测");
        fixture.Transform = page => { page["displayedPage"] = 2; return page; };
        await fixture.Round(monitor);
        Check(monitor.State()["issue"]!.GetValue<bool>(), "错页未拦截");
        fixture.Transform = page => { page["login"] = true; return page; };
        await fixture.Round(monitor);
        Check(monitor.State()["issue"]!.GetValue<bool>(), "登录页面被当成空库");
        fixture.Transform = null;
        fixture.Rows["/library/gifts"] = [[]];
        fixture.Rows["/library/free_downloads"] = [[]];
        await fixture.Round(monitor);
        Check(!monitor.State()["issue"]!.GetValue<bool>(), "真实空来源未识别");
        fixture.RateLimitPage = true;
        await fixture.Round(monitor);
        Check(monitor.State()["retryAt"] != null, "账号页面限流未全局退避");

        var migrationRoot = Path.Combine(root, "migration");
        Directory.CreateDirectory(migrationRoot);
        var versions = UpdateMonitor.FileVersions(Product(1), "1", ["11"]);
        System.IO.File.WriteAllText(Path.Combine(migrationRoot, "update-monitor.jsonl"), new JsonObject { ["id"] = "1", ["versions"] = versions }.ToJsonString() + "\n");
        var migration = new Fixture();
        migration.Update(1, migration.Time.AddMinutes(-5));
        using var upgraded = new UpdateMonitor(migrationRoot, migration.Handler, () => migration.Time);
        upgraded.SetLibrary(migration.Library);
        await upgraded.CheckBatchAsync(migration.Read, CancellationToken.None);
        Check(upgraded.State()["items"]!.AsArray().Count == 1, "未复用0.5.7文件基线");
        var combined = Item(1, "11");
        LibraryOwnership.Merge(combined, Item(1, "12"));
        Check(LibraryOwnership.Versions(combined).SequenceEqual(new[] { "11", "12" }), "跨来源拥有版本未合并");
    }

    private static async Task BoundaryCasesAsync(string root)
    {
        Directory.CreateDirectory(root);
        var fixture = new Fixture();
        using var monitor = new UpdateMonitor(root, fixture.Handler, () => fixture.Time);
        monitor.SetLibrary(fixture.Library);
        await monitor.CheckBatchAsync(fixture.Read, CancellationToken.None);
        var original = fixture.Time;
        for (var id = 100; id <= 106; id++) fixture.Update(id, original.AddMinutes(-10));
        fixture.Rows["/library"] = [[100, 101, 102, 103, 104, 105], [106, 1, 2, 3, 4, 5]];
        fixture.Reads.Clear();
        await fixture.Round(monitor);
        Check(fixture.Reads.Count == 4 && monitor.State()["items"]!.AsArray().Count == 0, "同时间边界被提前截断或历史变更误报");
        var at = fixture.Time.AddMinutes(1);
        for (var id = 100; id <= 106; id++) fixture.Update(id, at);
        await fixture.Round(monitor);
        Check(monitor.State()["items"]!.AsArray().Count == 7, "多件同秒跨页更新遗漏");
        fixture.Rows["/library/gifts"] = [[100, 1, 2, 3, 4, 5]];
        fixture.Reads.Clear();
        var calls = fixture.Handler.Calls;
        fixture.Update(100, fixture.Time.AddMinutes(1));
        await fixture.Round(monitor);
        Check(fixture.Handler.Calls - calls == 17 && monitor.State()["items"]!.AsArray().Count == 8, "跨来源重复商品重复请求或通知");
        var notices = monitor.State()["items"]!.AsArray().Count;
        fixture.Handler.Get(100)["variations"]![0]!["downloadables"]![0]!["updated_at"] = DateTimeOffset.Parse(fixture.Handler.Get(100)["variations"]![0]!["downloadables"]![0]!["updated_at"]!.ToString()).ToOffset(TimeSpan.FromHours(8)).ToString("O");
        await fixture.Round(monitor);
        Check(monitor.State()["items"]!.AsArray().Count == notices, "相同时刻的不同时区表示重复通知");
        var watermark = JsonNode.Parse(System.IO.File.ReadAllText(Path.Combine(root, "update-monitor-state.json")))!["sections"]!["bought"]!["through"]!.ToString();
        fixture.Time = fixture.Time.AddMinutes(16);
        using var cancellation = new CancellationTokenSource();
        async Task<JsonObject> CancelRead(string url, CancellationToken token)
        {
            var page = await fixture.Read(url, token);
            cancellation.Cancel();
            return page;
        }
        var cancelled = false;
        try { await monitor.CheckBatchAsync(CancelRead, cancellation.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled && !monitor.Busy && JsonNode.Parse(System.IO.File.ReadAllText(Path.Combine(root, "update-monitor-state.json")))!["sections"]!["bought"]!["through"]!.ToString() == watermark,
            "主同步/退出取消后推进边界或锁死后台检测");
        await fixture.Round(monitor);
        Check(!monitor.State()["issue"]!.GetValue<bool>(), "取消后无法恢复");
    }
}
