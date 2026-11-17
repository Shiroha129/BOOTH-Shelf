using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;

namespace BoothShelf;

internal static class CoreSelfTest
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal static async Task RunAsync(string root)
    {
        var report = new JsonObject();
        var run = Path.Combine(root, "run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(run);
        try
        {
            report["language"] = LanguageSelfTest.Run(Subdirectory(run, "language"));
            report["windowsStartup"] = WindowsStartupSelfTest.Run(Subdirectory(run, "windows-startup"));
            report["runtimeInstaller"] = await RuntimeInstallerSelfTest.RunAsync(Subdirectory(run, "runtime-installer"));
            report["appUpdateClient"] = await AppUpdateClientSelfTest.RunAsync(Subdirectory(run, "app-update-client"));
            report["appUpdateInstaller"] = await AppUpdateInstallerSelfTest.RunAsync(Subdirectory(run, "app-update-installer"));
            report["syncEngine"] = await SyncSelfTest.RunAsync(Path.Combine(run, "sync"));
            report["metadata"] = await FeatureSelfTest.MetadataAsync(Path.Combine(run, "metadata"));
            report["metadataConcurrency"] = await MetadataConcurrencyAsync(Subdirectory(run, "metadata-parallel"));
            report["sections"] = Sections(Subdirectory(run, "sections"));
            report["syncWorkflow"] = await SyncWorkflowSelfTest.RunAsync(Subdirectory(run, "workflow"));
            report["purchases"] = await PurchasesAsync(Subdirectory(run, "purchases"));
            report["parallelPurchases"] = await ConcurrencySelfTest.PurchasesAsync(Subdirectory(run, "parallel-purchases"));
            report["requestGate"] = await ConcurrencySelfTest.RequestsAsync();
            report["boothAccess"] = await BoothRequestPolicySelfTest.RunAsync(Subdirectory(run, "booth-access"));
            report["localFiles"] = LocalFiles(Subdirectory(run, "local"));
            report["localAssets"] = LocalAssetSelfTest.Run(Subdirectory(run, "local-assets"));
            report["currency"] = await CurrencyAsync(Subdirectory(run, "currency"));
            report["currencySchedule"] = await CurrencyScheduleSelfTest.RunAsync(Subdirectory(run, "currency-schedule"));
            report["filterPresets"] = FilterPresetSelfTest.Run(Subdirectory(run, "filter-presets"));
            report["updateNotifications"] = await UpdateMonitorSelfTest.RunAsync(Subdirectory(run, "notifications"));
            report["downloadedNotifications"] = await UpdateMonitorSelfTest.DownloadedAsync(Subdirectory(run, "downloaded-notifications"));
            report["freeOwnership"] = await FreeOwnershipSelfTest.RunAsync(Subdirectory(run, "free-ownership"));
            report["downloadSelection"] = await DownloadSelectionAsync(Subdirectory(run, "download-selection"));
            report["productDetails"] = await ProductDetailsAsync(Subdirectory(run, "product-details"));
            report["passed"] = true;
        }
        catch (Exception error)
        {
            report["passed"] = false;
            report["error"] = error.ToString();
            Environment.ExitCode = 1;
        }
        File.WriteAllText(Path.Combine(root, "core-result.json"), report.ToJsonString(new() { WriteIndented = true }));
    }

    private static string Subdirectory(string root, string name)
    {
        var path = Path.Combine(root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static JsonObject Item(string id, string section = "bought", string file = "100") => new()
    {
        ["id"] = id, ["title"] = "Asset " + id, ["shop"] = "Shop", ["section"] = section,
        ["files"] = new JsonArray(new JsonObject { ["id"] = file, ["name"] = file + ".zip" }),
        ["libraryUrl"] = LibraryCoordinator.Sections.First(value => value.Id == section).Url
    };

    private static JsonObject Sections(string root)
    {
        var sections = new JsonObject
        {
            ["bought"] = new JsonObject { ["items"] = new JsonArray(Item("1")), ["totalPages"] = 2 },
            ["gift"] = new JsonObject { ["items"] = new JsonArray(Item("1", "gift", "101"), Item("2", "gift", "102")), ["totalPages"] = 1 },
            ["free"] = new JsonObject { ["items"] = new JsonArray(Item("3", "free", "103")), ["totalPages"] = 1 }
        };
        var combined = LibraryCoordinator.Combine(sections, "full", true);
        Check(combined["items"]!.AsArray().Count == 3 && combined["totalPages"]!.GetValue<int>() == 4, "三来源合并数量错误");
        Check(combined["items"]![0]!["sections"]!.AsArray().Count == 2 && combined["items"]![0]!["files"]!.AsArray().Count == 2, "跨来源的文件或来源丢失");
        var baseline = LibraryCoordinator.Baseline(combined, "gift");
        Check(baseline["items"]!.AsArray().Count == 2, "礼物基线丢失");
        var first = new LibraryPage { Page = 1, MaxPage = 2, Items = new JsonArray(Item("1")) };
        var cache = SyncCache.Open(root, first, "full", "");
        cache.Save(new LibraryPage { Page = 2, MaxPage = 2, Items = new JsonArray(Item("1", file: "101")) });
        var merged = cache.Snapshot(null, true, false);
        Check(merged["items"]![0]!["files"]!.AsArray().Count == 2, "跨页变体文件丢失");
        Check(first.Fingerprint != cache.Pages[2].Fingerprint, "不同变体被误识别为重复页");
        new LibraryPage { Page = 1, MaxPage = 1, Empty = true }.Validate(1);
        var invalidRejected = false;
        try { new LibraryPage { Page = 1, MaxPage = 1 }.Validate(1); }
        catch (InvalidDataException) { invalidRejected = true; }
        Check(invalidRejected, "未知空白页未拦截");
        return new JsonObject { ["threeSections"] = true, ["sharedItemFiles"] = true, ["variantPagination"] = true, ["emptyPageValidation"] = true };
    }

    private static async Task<JsonObject> PurchasesAsync(string root)
    {
        var receipts = new Dictionary<string, JsonObject>
        {
            ["11"] = JsonNode.Parse("""{"id":"11","paid":true,"total":790,"lines":[{"itemId":"1","price":790,"boost":0}]}""")!.AsObject(),
            ["12"] = JsonNode.Parse("""{"id":"12","paid":true,"total":1910,"lines":[{"itemId":"2","price":1280,"boost":50},{"itemId":"3","price":580,"boost":0}]}""")!.AsObject(),
            ["13"] = JsonNode.Parse("""{"id":"13","paid":false,"canceled":true,"total":9999,"lines":[{"itemId":"1","price":9999}]}""")!.AsObject()
        };
        var detailCalls = 0;
        Task<JsonObject> Read(string url, string kind, CancellationToken token)
        {
            if (kind == "orders") return Task.FromResult(new JsonObject { ["page"] = 1, ["maxPage"] = 1, ["ids"] = new JsonArray("11", "12", "13") });
            detailCalls++;
            return Task.FromResult((JsonObject)receipts[new Uri(url).Segments.Last()].DeepClone());
        }
        var catalog = new PurchaseCatalog(root);
        await catalog.SyncAsync(Read, (_, _) => { }, CancellationToken.None);
        var snapshot = new JsonObject { ["items"] = new JsonArray(Item("1"), Item("2"), Item("3"), Item("4")) };
        catalog.Apply(snapshot);
        Check(snapshot["spend"]!["totalJpy"]!.GetValue<decimal>() == 2700 && snapshot["spend"]!["orders"]!.GetValue<int>() == 2, "累计支付包含取消订单或重复计费");
        Check(snapshot["items"]![1]!["purchaseJpy"]!.GetValue<decimal>() == 1330 && snapshot["items"]![3]!["purchaseJpy"] == null, "成交价/BOOST 或未确认金额错误");
        catalog = new PurchaseCatalog(root);
        await catalog.SyncAsync(Read, (_, _) => { }, CancellationToken.None);
        catalog.Apply(snapshot);
        Check(detailCalls == 4 && snapshot["spend"]!["totalJpy"]!.GetValue<decimal>() == 2700, "已支付缓存未复用或重复累加");
        var newCalls = new List<string>();
        Task<JsonObject> ReadNew(string url, string kind, CancellationToken token)
        {
            newCalls.Add(url);
            if (kind == "orders")
            {
                Check(url.EndsWith("?page=1"), "增量金额扫描了历史订单页");
                return Task.FromResult(new JsonObject { ["page"] = 1, ["maxPage"] = 900, ["ids"] = new JsonArray("14") });
            }
            return Task.FromResult(JsonNode.Parse("""{"id":"14","paid":true,"total":400,"lines":[{"itemId":"4","price":400,"boost":0}]}""")!.AsObject());
        }
        await catalog.SyncAsync(ReadNew, (_, _) => { }, CancellationToken.None, new HashSet<string> { "4" });
        catalog.Apply(snapshot);
        Check(newCalls.Count == 2 && snapshot["spend"]!["totalJpy"]!.GetValue<decimal>() == 3100
            && snapshot["spend"]!["complete"]!.GetValue<bool>(), "新增成交价未及时停止或丢失历史总额");
        await catalog.SyncAsync(ReadNew, (_, _) => { }, CancellationToken.None, new HashSet<string>());
        await catalog.SyncAsync(ReadNew, (_, _) => { }, CancellationToken.None, new HashSet<string> { "4" });
        Check(newCalls.Count == 2, "无新增或已缓存金额仍访问订单网页");
        var partialRoot = Subdirectory(root, "partial");
        var partial = new PurchaseCatalog(partialRoot);
        await partial.SyncAsync(ReadNew, (_, _) => { }, CancellationToken.None, new HashSet<string> { "4" });
        var unknown = new JsonObject { ["items"] = new JsonArray(Item("4"), Item("99")) };
        unknown["items"]![1]!["purchaseJpy"] = 123m;
        new PurchaseCatalog(partialRoot).Apply(unknown);
        Check(!unknown["spend"]!["complete"]!.GetValue<bool>() && unknown["spend"]!["orders"]!.GetValue<int>() == 1
            && unknown["items"]![1]!["purchaseJpy"]!.GetValue<decimal>() == 123, "部分金额冒充全量或抹去旧成交价");
        return new JsonObject { ["paidTotals"] = true, ["boost"] = true, ["canceledExcluded"] = true, ["unknownIsNotZero"] = true,
            ["cacheAfterRestart"] = true, ["incrementalStopsEarly"] = true, ["unchangedSkipsOrders"] = true, ["partialTotalsMarked"] = true };
    }

    private static JsonObject LocalFiles(string root)
    {
        var path = Path.Combine(root, "test.zip");
        File.WriteAllBytes(path, [1, 2, 3]);
        var record = new JsonObject { ["id"] = "download1", ["itemId"] = "1", ["path"] = path, ["name"] = "test.zip", ["state"] = "completed" };
        var catalog = new LocalLibrary(root, new JsonArray(record));
        catalog.Add(record);
        Check(catalog.List().Count == 1 && catalog.List()[0]!["size"]!.GetValue<long>() == 3, "本地库迁移重复或大小不符");
        Check(catalog.Resolve("download1") == path, "本地文件定位错误");
        var rejected = false;
        try { catalog.Resolve("unregistered"); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "允许操作未登记文件");
        File.Move(path, path + ".moved");
        Check(catalog.List().Count == 0, "不存在文件仍出现在本地库");
        var deleted = (JsonObject)record.DeepClone();
        deleted["state"] = "deleted";
        File.WriteAllText(Path.Combine(root, "local-files.json"), new JsonArray(deleted).ToJsonString());
        File.Move(path + ".moved", path);
        var restored = new LocalLibrary(root, new JsonArray());
        Check(restored.List().Count == 0, "已删除记录重新出现");
        var redownload = (JsonObject)record.DeepClone();
        redownload["id"] = "download2";
        restored.Add(redownload);
        Check(restored.List().Count == 1 && restored.Resolve("download2") == path, "同路径重新下载无法登记");
        return new JsonObject { ["migration"] = true, ["noDuplicates"] = true, ["missingFile"] = true, ["registeredFilesOnly"] = true, ["redownload"] = true };
    }

    private sealed class TestHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }

    private static async Task<JsonObject> CurrencyAsync(string root)
    {
        var calls = 0;
        using var handler = new TestHandler((request, _) =>
        {
            calls++;
            Check(request.RequestUri!.Query == "?base=JPY", "汇率请求包含多余数据");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(calls == 1 ? """{"base":"JPY","date":"2026-11-06","rates":{"CNY":0.042137,"USD":0.0064}}""" : """{"base":"USD","date":"bad"}""")
            });
        });
        using var service = new CurrencyService(root, handler);
        var first = await service.RefreshAsync(false, CancellationToken.None);
        Check(first["rates"]!["CNY"]!.GetValue<decimal>() == 0.042137m, "汇率方向错误");
        await service.RefreshAsync(false, CancellationToken.None);
        Check(calls == 1, "24 小时缓存未生效");
        var failed = await service.RefreshAsync(true, CancellationToken.None);
        Check(failed["error"] != null && failed["rates"]!["CNY"]!.GetValue<decimal>() == 0.042137m, "失败后丢失汇率缓存");
        using var restored = new CurrencyService(root);
        Check(restored.Rates["rates"]!["JPY"]!.GetValue<decimal>() == 1, "汇率重启缓存无效");
        return new JsonObject { ["rateDirection"] = true, ["cache"] = true, ["offlineFallback"] = true };
    }

    private static async Task<JsonObject> MetadataConcurrencyAsync(string root)
    {
        var calls = 0;
        var active = 0;
        var peak = 0;
        using var handler = new TestHandler(async (request, token) =>
        {
            Interlocked.Increment(ref calls);
            var count = Interlocked.Increment(ref active);
            lock (root) peak = Math.Max(peak, count);
            try
            {
                await Task.Delay(15, token);
                var id = Path.GetFileNameWithoutExtension(request.RequestUri!.AbsolutePath);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"id\":" + id + ",\"tags\":[{\"name\":\"VRChat\"}]}") };
            }
            finally { Interlocked.Decrement(ref active); }
        });
        using var catalog = new MetadataCatalog(root, handler);
        var items = new JsonArray(Enumerable.Range(1, 30).Select(number => (JsonNode)new JsonObject { ["id"] = ((number + 1) / 2).ToString() }).ToArray());
        await catalog.EnrichAsync(items, CancellationToken.None);
        Check(calls == 15 && peak == 12, "标签并发限制或去重错误");
        Check(items.All(item => item!["tags"]!.AsArray().Count == 1), "同步完成前标签未到位");
        return new JsonObject { ["requests"] = calls, ["peak"] = peak, ["allEnrichedBeforeReturn"] = true };
    }

    private static async Task<JsonObject> DownloadSelectionAsync(string root)
    {
        using var service = new DownloadService(root, () => throw new InvalidOperationException("缓存选择无需浏览器"), () => root, CancellationToken.None,
            sizeHandler: new TestHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));
        var item = Item("1");
        item["files"]!.AsArray().Add(new JsonObject { ["id"] = "101", ["name"] = "Materials.zip", ["material"] = true });
        var preferredNames = new[] { "Outfit_MILFY.zip", "ミルフィ衣装.zip", "みるふぃ.zip", "Outfit_エク.zip", "Outfit_eku_v2.zip", "ﾐﾙﾌｨ.zip", "ＥＫＵ.zip", "えく.zip" };
        for (var index = 0; index < preferredNames.Length; index++)
            item["files"]!.AsArray().Add(new JsonObject { ["id"] = (index + 102).ToString(), ["name"] = preferredNames[index], ["material"] = false });
        item["files"]!.AsArray().Add(new JsonObject { ["id"] = "110", ["name"] = "Outfit_Milfy_Materials.zip", ["material"] = true });
        item["files"]!.AsArray().Add(new JsonObject { ["id"] = "111", ["name"] = "Manuka.zip", ["material"] = false });
        var selection = await service.ListAsync(item);
        Check(selection["files"]![0]!["material"]!.GetValue<bool>() && selection["files"]![0]!["name"]!.GetValue<string>() == "Materials.zip", "材质文件未置顶");
        var ordered = selection["files"]!.AsArray();
        Check(ordered[1]!["id"]!.GetValue<string>() == "110"
            && ordered.Skip(2).Take(preferredNames.Length).Select(file => file!["name"]!.GetValue<string>()).SequenceEqual(preferredNames)
            && ordered[^2]!["id"]!.GetValue<string>() == "100" && ordered[^1]!["id"]!.GetValue<string>() == "111", "偏好文件排序或原顺序错误");
        Check(ordered.Skip(2).All(file => file!["material"]?.GetValue<bool>() != true), "偏好文件被错误标记为材质文件");
        Check(!selection.ToJsonString().Contains("downloadables"), "文件选择向界面泄露下载地址");
        var rejected = false;
        try { service.Enqueue(selection["ticket"]!.GetValue<string>(), ["999"]); } catch (InvalidDataException) { rejected = true; }
        Check(rejected && service.History.Count == 0, "允许未选文件加入下载");
        var folder = Path.Combine(root, DownloadService.SafeName("1_" + item["title"]!.GetValue<string>()));
        Directory.CreateDirectory(folder);
        var localPath = Path.Combine(folder, "Materials.zip");
        File.WriteAllText(localPath, "original");
        var local = new JsonObject { ["itemId"] = "1", ["title"] = item["title"]!.DeepClone(), ["path"] = localPath };
        void RejectUpdate(string ticket, string[] names, JsonObject destination)
        {
            var failed = false;
            try { service.EnqueueUpdate(ticket, names, destination); }
            catch (Exception error) when (error is InvalidOperationException or InvalidDataException or FileNotFoundException) { failed = true; }
            Check(failed && service.History.Count == 0, "无效更新通知加入了下载队列");
        }
        var validTicket = selection["ticket"]!.GetValue<string>();
        RejectUpdate("missing", ["Materials.zip"], local);
        RejectUpdate(validTicket, [], local);
        RejectUpdate(validTicket, ["Materials.zip", "missing.zip"], local);
        RejectUpdate(validTicket, ["Materials"], local);
        var wrongAsset = (JsonObject)local.DeepClone();
        wrongAsset["itemId"] = "2";
        RejectUpdate(validTicket, ["Materials.zip"], wrongAsset);
        wrongAsset = (JsonObject)local.DeepClone();
        wrongAsset["title"] = "Wrong folder";
        RejectUpdate(validTicket, ["Materials.zip"], wrongAsset);
        File.Delete(localPath);
        RejectUpdate(validTicket, ["Materials.zip"], local);
        File.WriteAllText(localPath, "original");
        item["files"]!.AsArray().Add(new JsonObject { ["id"] = "112", ["name"] = "Materials.zip" });
        var ambiguous = await service.ListAsync(item);
        RejectUpdate(ambiguous["ticket"]!.GetValue<string>(), ["Materials.zip"], local);
        return new JsonObject { ["cachedFileList"] = true, ["materialFirst"] = true, ["preferredNamesAfterMaterials"] = true,
            ["stableFileOrder"] = true, ["noExtraMaterialFlags"] = true, ["selectionValidated"] = true,
            ["updateNamesValidated"] = true, ["updateDestinationValidated"] = true };
    }

    private static async Task<JsonObject> ProductDetailsAsync(string root)
    {
        var sizeCalls = 0;
        var changed = false;
        var unavailable = false;
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send = (request, _) =>
        {
            var uri = request.RequestUri!;
            if (uri.Host == "api.booth.pm")
            {
                Check(uri.AbsolutePath == "/vroid/items/123", "大小查询未使用商品 ID");
                sizeCalls++;
                if (unavailable) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($$"""
                    {"id":123,"variations":[{"id":10,"downloadables":[{"name":"Materials.zip","file_size":"{{(changed ? "9" : "8")}} MB"},{"name":"Common.zip","file_size":"1 MB"}]},{"id":11,"downloadables":[{"name":"Common.zip","file_size":"2 MB"}]}]}
                    """) });
            }
            Check(uri.Host == "booth.pm" && uri.AbsolutePath is "/ja/items/123.json" or "/ja/items/404.json", "详情查询地址无效");
            return Task.FromResult(uri.AbsolutePath.Contains("404") ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""
                    {"id":123,"tags":[],"name":"Original title","description":"Original description\n<script>literal text</script>","images":[{"original":"https://example.test/original.jpg","resized":"https://example.test/small.jpg"},{"resized":"https://example.test/second.jpg"}],"variations":[{"name":"Full Set","price":2000},{"name":"Milfy/Eku","price":1000}]}
                    """) });
        };
        using (var catalog = new MetadataCatalog(root, new TestHandler(send)))
        {
            var detail = await catalog.DetailAsync("123", CancellationToken.None);
            Check(detail["title"]!.GetValue<string>() == "Original title" && detail["description"]!.GetValue<string>().Contains("<script>literal text</script>"), "原商品文字未保留");
            Check(detail["images"]![0]!.GetValue<string>() == "https://example.test/original.jpg" && detail["images"]![1]!.GetValue<string>() == "https://example.test/second.jpg", "商品原图或备用图映射错误");
            Check(detail["variations"]!.AsArray().Count == 2 && detail["variations"]![1]!["name"]!.GetValue<string>() == "Milfy/Eku", "商品选项映射错误");
            var rejected = false;
            try { await catalog.DetailAsync("../123", CancellationToken.None); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "允许无效商品 ID");
            rejected = false;
            try { await catalog.DetailAsync("404", CancellationToken.None); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "不可用商品未提示失败");
        }
        var savedFolder = Path.Combine(root, "previous-download-directory", "asset");
        var imageBytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+j4ZkAAAAASUVORK5CYII=");
        var imageType = "image/png";
        var imageStatus = HttpStatusCode.OK;
        var emptyImage = false;
        var oversizedImage = false;
        var imageCalls = 0;
        using (var images = new MetadataCatalog(root, new TestHandler((request, _) =>
        {
            imageCalls++;
            Check(request.RequestUri!.Host == "booth.pximg.net" && request.Headers.Referrer?.Host == "booth.pm", "图片下载地址或来源无效");
            var content = new ByteArrayContent(emptyImage ? [] : imageBytes);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(imageType);
            if (oversizedImage) content.Headers.ContentLength = 50 * 1024 * 1024 + 1;
            return Task.FromResult(new HttpResponseMessage(imageStatus) { Content = content });
        })))
        {
            const string imageUrl = "https://booth.pximg.net/original.png";
            var image = await images.ImageAsync(imageUrl, CancellationToken.None);
            Check(image.Data.SequenceEqual(imageBytes) && image.Extension == ".png", "图片保存未保留原始内容或格式");
            var callsBeforeInvalidLinks = imageCalls;
            foreach (var url in new[] { "file:///C:/private.png", "http://booth.pximg.net/image.png", "https://127.0.0.1/image.png", "https://booth.pximg.net.evil.test/image.png", "https://user@booth.pximg.net/image.png" })
            {
                var rejected = false;
                try { await images.ImageAsync(url, CancellationToken.None); } catch (InvalidDataException) { rejected = true; }
                Check(rejected, "图片下载允许不受信任的地址");
            }
            Check(imageCalls == callsBeforeInvalidLinks, "拒绝图片地址前已发起网络请求");
            imageType = "text/html";
            var invalidImage = false;
            try { await images.ImageAsync(imageUrl, CancellationToken.None); } catch (InvalidDataException) { invalidImage = true; }
            Check(invalidImage, "网页响应被保存为图片");
            imageType = "image/png";
            emptyImage = true;
            invalidImage = false;
            try { await images.ImageAsync(imageUrl, CancellationToken.None); } catch (InvalidDataException) { invalidImage = true; }
            Check(invalidImage, "空文件被保存为图片");
            emptyImage = false;
            imageStatus = HttpStatusCode.NotFound;
            invalidImage = false;
            try { await images.ImageAsync(imageUrl, CancellationToken.None); } catch (InvalidOperationException) { invalidImage = true; }
            Check(invalidImage, "图片下载失败未返回错误");
            imageStatus = HttpStatusCode.OK;
            oversizedImage = true;
            invalidImage = false;
            try { await images.ImageAsync(imageUrl, CancellationToken.None); } catch (HttpRequestException) { invalidImage = true; }
            Check(invalidImage, "图片响应超过大小上限仍被读取");
        }
        Directory.CreateDirectory(savedFolder);
        File.WriteAllText(Path.Combine(root, "downloads.json"), new JsonArray(
            new JsonObject { ["id"] = "saved", ["path"] = Path.Combine(savedFolder, "old.zip"), ["state"] = "completed" },
            new JsonObject { ["id"] = "unsaved", ["state"] = "canceled" },
            new JsonObject { ["id"] = "moved", ["path"] = Path.Combine(root, "missing-directory", "old.zip"), ["state"] = "completed" }).ToJsonString());
        using var sizes = new TestHandler(send);
        using var service = new DownloadService(root, () => throw new InvalidOperationException("离线测试不得启动浏览器"), () => root, CancellationToken.None, sizeHandler: sizes);
        var item = new JsonObject { ["id"] = "123", ["title"] = "Outfit", ["section"] = "bought", ["files"] = new JsonArray(
            new JsonObject { ["id"] = "101", ["name"] = "Materials.zip", ["material"] = true, ["variationIds"] = new JsonArray("10") },
            new JsonObject { ["id"] = "102", ["name"] = "Common.zip", ["variationIds"] = new JsonArray("10") },
            new JsonObject { ["id"] = "103", ["name"] = "Common.zip" },
            new JsonObject { ["id"] = "104", ["name"] = "Known.zip", ["size"] = 512 }) };
        var selection = await service.ListAsync(item);
        var files = selection["files"]!.AsArray().OfType<JsonObject>().ToDictionary(file => file["id"]!.GetValue<string>());
        Check(sizeCalls == 1 && files["101"]["size"]!.GetValue<string>() == "8 MB", "每文件大小未用单次查询补齐");
        Check(files["102"]["size"]!.GetValue<string>() == "1 MB" && files["103"]["size"] == null && files["104"]["size"]!.GetValue<int>() == 512, "大小匹配未区分已购选项或覆盖已知大小");
        Check(!selection.ToJsonString().Contains("downloadables"), "大小预览泄露下载地址");
        changed = true;
        selection = await service.ListAsync(item);
        Check(sizeCalls == 2 && selection["files"]![0]!["size"]!.GetValue<string>() == "9 MB", "重复打开文件列表仍使用旧大小");
        unavailable = true;
        selection = await service.ListAsync(item);
        Check(selection["files"]!.AsArray().Count == 4 && selection["files"]![0]!["size"] == null, "大小查询失败阻止选择已购文件");
        var calls = sizeCalls;
        await service.ListAsync(new JsonObject { ["id"] = "123", ["title"] = "Known", ["section"] = "bought", ["files"] = new JsonArray(new JsonObject { ["id"] = "105", ["name"] = "Known.zip", ["size"] = 0 }) });
        Check(sizeCalls == calls, "已知大小的文件仍查询接口");
        Check(service.GetFolder("saved") == savedFolder, "下载记录目录被当前设置替换");
        foreach (var id in new[] { "unknown", "unsaved", "moved" })
        {
            var rejected = false;
            try { service.GetFolder(id); } catch (Exception error) when (error is InvalidOperationException or DirectoryNotFoundException) { rejected = true; }
            Check(rejected, "无效下载记录仍打开目录");
        }
        return new JsonObject { ["originalProductFields"] = true, ["invalidProductRejected"] = true, ["sizeMatchedToOwnership"] = true,
            ["sizeRefreshesWhenReopened"] = true, ["unknownSizeAllowsSelection"] = true, ["recordDirectoryValidated"] = true,
            ["originalImageBytesAndFormat"] = true, ["imageOriginsValidatedBeforeRequests"] = true,
            ["failedEmptyAndNonImageRejected"] = true, ["imageSizeLimited"] = true };
    }
}
