using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace BoothShelf;

internal static class FeatureSelfTest
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("新功能检查失败：" + message);
    }

    private sealed class MetadataHandler : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            var id = Path.GetFileNameWithoutExtension(request.RequestUri!.AbsolutePath);
            var response = id == "404" ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($$$"""
                        {"id":{{{id}}},"tags":[{"name":"VRChat"},{"name":"衣装"},{"name":"VRChat"}],"category":{"name":"3D衣装","parent":{"name":"3Dモデル"}},"is_adult":false,"variations":[{"type":"digital"}]}
                        """)
                };
            return Task.FromResult(response);
        }
    }

    internal static async Task<JsonObject> MetadataAsync(string root)
    {
        Directory.CreateDirectory(root);
        var handler = new MetadataHandler();
        using (var catalog = new MetadataCatalog(root, handler))
        {
            await catalog.EnrichAsync(new JsonArray(new JsonObject { ["id"] = "123" }, new JsonObject { ["id"] = "404" }), CancellationToken.None);
            Check(handler.Calls == 2, "详情请求次数错误");
            var snapshot = new JsonObject { ["items"] = new JsonArray(new JsonObject { ["id"] = "123" }, new JsonObject { ["id"] = "404" }) };
            catalog.Merge(snapshot);
            Check(snapshot["items"]![0]!["tags"]!.AsArray().Count == 2 && snapshot["items"]![0]!["category"]!.GetValue<string>() == "3Dモデル"
                && snapshot["items"]![0]!["subcategory"]!.GetValue<string>() == "3D衣装"
                && snapshot["items"]![0]!["adult"]!.GetValue<bool>() == false
                && snapshot["items"]![1]!["metadataStatus"]!.GetValue<string>() == "unavailable", "真实字段映射失败");
        }
        using var restored = new MetadataCatalog(root, handler = new MetadataHandler());
        await restored.EnrichAsync(new JsonArray(new JsonObject { ["id"] = "123" }), CancellationToken.None);
        Check(handler.Calls == 0, "已缓存商品重复请求");
        return new JsonObject { ["normalization"] = true, ["cacheAfterRestart"] = true, ["unavailableProduct"] = true };
    }

    internal static async Task<JsonObject> DownloadsAsync(CoreWebView2Environment environment, string root)
    {
        Directory.CreateDirectory(root);
        var directory = Path.Combine(root, "downloads");
        var fetched = new List<string>();
        var loggedOut = false;
        var downloadDenied = false;
        const string html = """
            <title>Library</title><main>
            <article><a href="https://artist.booth.pm/items/123">测试商品</a>
            <div class="mt-16">
              <div><span>one.zip</span><div><div class="js-download-button" data-href="https://booth.pm/downloadables/101"><button>Download</button></div><div class="js-download-button"><button>Other Downloads</button></div></div></div>
              <div><span>two.zip</span><div><div class="js-download-button" data-href="https://booth.pm/downloadables/102"><button>Download</button></div></div></div>
            </div></article>
            <article><a href="https://artist.booth.pm/items/456">其他商品</a><div><span>private.zip</span><a href="https://booth.pm/downloadables/999">Download</a></div></article></main>
            """;
        void Configure(SessionBrowser browser)
        {
            browser.View.CoreWebView2.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            browser.View.CoreWebView2.WebResourceRequested += (_, args) =>
            {
                var uri = new Uri(args.Request.Uri);
                if (uri.Host == "booth.pm" && uri.AbsolutePath.StartsWith("/downloadables/"))
                {
                    fetched.Add(uri.AbsolutePath);
                    args.Response = downloadDenied
                        ? environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes("Denied")), 403, "Forbidden", "Content-Type: text/html\r\n")
                        : environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes("BOOTH-Shelf synthetic download " + uri.AbsolutePath)), 200, "OK",
                            "Content-Type: application/octet-stream\r\nContent-Disposition: attachment; filename=\"test.zip\"\r\nCache-Control: no-store\r\n");
                }
                else
                    args.Response = environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(loggedOut ? "<input type='password'>" : html)), 200, "OK",
                        "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\n");
            };
        }
        using var service = new DownloadService(root, () => Task.FromResult(environment), () => directory, CancellationToken.None, Configure);
        var item = new JsonObject { ["id"] = "123", ["title"] = "测试商品", ["libraryUrl"] = ShelfWindow.LibraryUrl };
        var selection = await service.ListAsync(item);
        Check(selection["files"]!.AsArray().Count == 2 && selection["files"]![0]!["name"]!.GetValue<string>() == "one.zip", "文件名解析或商品隔离失败");
        Check(!selection.ToJsonString().Contains("downloadables"), "界面泄漏文件地址");
        var ticket = selection["ticket"]!.GetValue<string>();
        var rejected = false;
        try { service.Enqueue(ticket, ["999"]); } catch (InvalidDataException) { rejected = true; }
        Check(rejected && service.History.Count == 0, "允许下载其他商品文件");
        service.Enqueue(ticket, ["102"]);
        await WaitDownloadsAsync(service);
        Check(service.History[0]!["state"]!.GetValue<string>() == "completed", "文件传输未完成：" + service.History.ToJsonString());
        var firstPath = service.History[0]!["path"]!.GetValue<string>();
        Check(File.ReadAllText(firstPath).EndsWith("/102") && Path.GetFileName(firstPath) == "two.zip" && !File.Exists(firstPath + ".part")
            && fetched.SequenceEqual(new[] { "/downloadables/102" }), "选择下载或完成重命名失败");
        service.Enqueue(ticket, ["102"]);
        await WaitDownloadsAsync(service);
        Check(service.History[0]!["path"]!.GetValue<string>() != firstPath && File.Exists(firstPath), "重复下载覆盖原文件");
        directory = Path.Combine(root, "changed-directory");
        service.Enqueue(ticket, ["101"]);
        await WaitDownloadsAsync(service);
        Check(service.History[0]!["path"]!.GetValue<string>().StartsWith(directory + Path.DirectorySeparatorChar)
            && service.History[0]!["state"]!.GetValue<string>() == "completed", "目录更改未生效");
        service.Enqueue(ticket, ["101", "102"]);
        var queuedId = service.History[0]!["id"]!.GetValue<string>();
        service.Cancel(queuedId);
        await WaitDownloadsAsync(service);
        Check(service.History[0]!["state"]!.GetValue<string>() == "canceled", "队列取消失败");
        service.Enqueue(ticket, ["101"]);
        service.Cancel(service.History[0]!["id"]!.GetValue<string>());
        await WaitDownloadsAsync(service);
        Check(service.History[0]!["state"]!.GetValue<string>() == "canceled", "当前任务取消失败");
        downloadDenied = true;
        service.Enqueue(ticket, ["101"]);
        await WaitDownloadsAsync(service);
        Check(service.History[0]!["state"]!.GetValue<string>() == "failed"
            && !File.Exists(service.History[0]!["path"]!.GetValue<string>()), "拒绝下载未正确失败或写出假文件");
        loggedOut = true;
        rejected = false;
        try { await service.ListAsync(item); } catch (InvalidOperationException error) { rejected = error.Message.Contains("登录"); }
        Check(rejected, "登录过期未拦截");
        Check(!File.ReadAllText(Path.Combine(root, "downloads.json")).Contains("downloadables"), "历史记录保存了下载地址");
        Check(DownloadService.SafeName("../CON?.zip").IndexOfAny(Path.GetInvalidFileNameChars()) < 0
            && DownloadService.SafeName("CON.zip") == "_CON.zip", "文件名净化失败");
        return new JsonObject { ["fileSelection"] = true, ["authenticatedListing"] = true, ["webViewDownload"] = true,
            ["noOverwrite"] = true, ["directoryChange"] = true, ["expiredLogin"] = true, ["safeFilename"] = true,
            ["cancelQueuedAndActive"] = true, ["downloadFailure"] = true };
    }

    private static async Task WaitDownloadsAsync(DownloadService service)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while (service.Busy) await Task.Delay(100, timeout.Token);
    }

    internal static async Task<JsonObject> InterfaceAsync(WebView2 view)
    {
        var result = await view.ExecuteScriptAsync("""
            (() => {
              const item = {id:'1',title:'Blue Dress',shop:'Artist',tags:['VRChat','衣装','VRChat'],category:'3Dモデル',subcategory:'3D衣装',forms:['digital'],adult:false};
              const counts = LibraryFilters.tagCounts([item,{tags:['VRChat']},{tags:['Other']}]);
              const checks = {
                allKeywords: LibraryFilters.matches(item,{all:'blue VRCHAT'}) && !LibraryFilters.matches(item,{all:'blue missing'}),
                anyKeywords: LibraryFilters.matches(item,{any:'red blue'}) && !LibraryFilters.matches(item,{any:'red black'}),
                exclusions: !LibraryFilters.matches(item,{exclude:'dress'}) && LibraryFilters.matches(item,{exclude:'red'}),
                multiselect: LibraryFilters.matches(item,{tags:['VRChat','衣装']}) && !LibraryFilters.matches(item,{tags:['VRChat','Other']}),
                tagCounts: counts[0][0] === 'VRChat' && counts[0][1] === 2 && counts[1][1] === 1,
                facets: LibraryFilters.matches(item,{category:'3Dモデル',subcategory:'3D衣装',form:'digital',age:'all'}) && !LibraryFilters.matches(item,{age:'adult'}) && !LibraryFilters.matches({}, {age:'all'}),
                login: (setAuth(true), element('login').hidden && !element('auth-state').hidden),
                simplified: !document.querySelector('.favorite') && !document.getElementById('favorites-view') && element('view-title').textContent === '资料库'
              };
              setSnapshot({items:[{...item,url:'https://booth.pm/ja/items/1',image:''},{id:'2',title:'Red Dress',shop:'Artist',tags:['Other'],image:''}],syncedAt:new Date().toISOString(),complete:true});
              document.querySelector('.tag-button').click();
              checks.tagButtons = document.querySelectorAll('.card').length === 1 && selectedTags.size === 1;
              element('clear-filters').click();
              checks.clear = selectedTags.size === 0 && document.querySelectorAll('.card').length === 2;
              showView('settings');
              checks.settings = !element('settings-panel').hidden && element('library-panel').hidden;
              showView('library');
              element('sync-progress').hidden = false;
              notify('同步完成',false,true);
              return checks;
            })()
            """);
        var report = JsonNode.Parse(result)!.AsObject();
        Check(report.All(entry => entry.Value?.GetValue<bool>() == true), "筛选或界面检查失败：" + result);
        await Task.Delay(10200);
        Check(await view.ExecuteScriptAsync("element('sync-status').hidden && element('sync-progress').hidden") == "true", "完成提示未在十秒后隐藏");
        await view.ExecuteScriptAsync("notify('已完成',false,true); notify('新的进度');");
        await Task.Delay(10200);
        Check(await view.ExecuteScriptAsync("!element('sync-status').hidden") == "true", "旧计时器隐藏了新进度");
        report["completionTimer"] = true;
        report["timerReset"] = true;
        await view.ExecuteScriptAsync("""
            globalThis.savedRequest = desktopRequest;
            globalThis.queuedFiles = null;
            desktopRequest = async (action,payload) => {
              if (action === 'files') return {ticket:'test',directory:'C:\\Downloads\\BOOTH-Shelf',files:[{id:'101',name:'选中的衣装.zip'},{id:'102',name:'另一款式.zip'}]};
              if (action === 'downloads-start') { globalThis.queuedFiles = payload.ids; return payload.ids.length; }
              return savedRequest(action,payload);
            };
            openFiles(snapshot.items[0]);
            """);
        await Task.Delay(150);
        var modal = await view.ExecuteScriptAsync("""
            (() => {
              const inputs = [...element('file-list').querySelectorAll('input')];
              if (!element('file-dialog').open || inputs.length !== 2 || !element('download-selected').disabled) return false;
              inputs[0].checked = true;
              inputs[0].dispatchEvent(new Event('change'));
              element('download-selected').click();
              return true;
            })()
            """);
        await Task.Delay(150);
        Check(modal == "true" && await view.ExecuteScriptAsync("queuedFiles?.join(',') === '101' && !element('file-dialog').open && !element('downloads-panel').hidden") == "true",
            "文件勾选窗口没有仅下载所选文件");
        await view.ExecuteScriptAsync("desktopRequest = savedRequest; delete globalThis.savedRequest; showView('library');");
        report["fileDialogSelection"] = true;
        return report;
    }
}
