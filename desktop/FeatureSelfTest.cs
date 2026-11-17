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
        using var service = new DownloadService(root, () => Task.FromResult(environment), () => directory, CancellationToken.None, Configure, sizeHandler: new MetadataHandler());
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
        Check(service.History[0]!["fileId"]?.GetValue<string>() == "102", "下载记录未保留 BOOTH 文件标识");
        var firstPath = service.History[0]!["path"]!.GetValue<string>();
        var original = service.History[0]!.AsObject();
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
        var local = new LocalLibrary(root, service.History);
        service.Completed += record => local.Add(record);
        item["title"] = "商品改名后";
        item["files"] = new JsonArray(new JsonObject { ["id"] = "999", ["name"] = "two.zip", ["size"] = 1 });
        var refreshed = await service.ListAsync(item, refresh: true);
        Check(refreshed["files"]!.AsArray().Any(file => file?["id"]?.GetValue<string>() == "102")
            && refreshed["files"]!.AsArray().All(file => file?["id"]?.GetValue<string>() != "999"), "更新下载使用了过期缓存");
        var fetchedBeforeUpdate = fetched.Count;
        var updateTicket = refreshed["ticket"]!.GetValue<string>();
        Check(service.EnqueueUpdate(updateTicket, ["two.zip", "two.zip"], original) == 1, "更新通知重复选择同一文件");
        rejected = false;
        try { service.EnqueueUpdate(updateTicket, ["two.zip"], original); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "更新下载允许重复排队");
        await WaitDownloadsAsync(service);
        var updated = service.History[0]!;
        Check(updated["state"]!.GetValue<string>() == "completed" && Path.GetDirectoryName(updated["path"]!.GetValue<string>()) == Path.GetDirectoryName(firstPath)
            && updated["title"]!.GetValue<string>() == original["title"]!.GetValue<string>()
            && File.ReadAllText(firstPath).EndsWith("/102") && updated["path"]!.GetValue<string>() != firstPath, "更新未保存到原资产目录或覆盖了原文件");
        Check(fetched.Skip(fetchedBeforeUpdate).SequenceEqual(new[] { "/downloadables/102" })
            && local.List("123").Any(file => file?["id"]?.GetValue<string>() == updated["id"]!.GetValue<string>())
            && local.AssetDirectories("123").Contains(Path.GetDirectoryName(firstPath)), "更新文件选择或本地库登记失败");
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
        try { await service.ListAsync(item, refresh: true); } catch (InvalidOperationException error) { rejected = error.Message.Contains("登录"); }
        Check(rejected, "登录过期未拦截");
        Check(!File.ReadAllText(Path.Combine(root, "downloads.json")).Contains("downloadables"), "历史记录保存了下载地址");
        Check(DownloadService.SafeName("../CON?.zip").IndexOfAny(Path.GetInvalidFileNameChars()) < 0
            && DownloadService.SafeName("CON.zip") == "_CON.zip", "文件名净化失败");
        return new JsonObject { ["fileSelection"] = true, ["authenticatedListing"] = true, ["webViewDownload"] = true,
            ["noOverwrite"] = true, ["directoryChange"] = true, ["expiredLogin"] = true, ["safeFilename"] = true,
            ["cancelQueuedAndActive"] = true, ["downloadFailure"] = true, ["updateFreshFiles"] = true,
            ["updateOriginalDirectory"] = true, ["updateNoDuplicates"] = true, ["updateLocalLibrary"] = true };
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
              element('more-tags').click();
              document.querySelector('.tag-button').click();
              checks.tagButtons = document.querySelectorAll('.card').length === 1 && selectedTags.size === 1;
              element('clear-filters').click();
              checks.clear = selectedTags.size === 0 && document.querySelectorAll('.card').length === 2;
              showView('settings');
              checks.settings = !element('settings-panel').hidden && element('library-panel').hidden;
              checks.startupOption = !element('start-with-windows').checked && element('start-with-windows').closest('#settings-panel') !== null;
              showView('library');
              element('sync-progress').hidden = false;
              notify('同步完成',false,true);
              return checks;
            })()
            """);
        var report = JsonNode.Parse(result)!.AsObject();
        Check(report.All(entry => entry.Value?.GetValue<bool>() == true), "筛选或界面检查失败：" + result);
        await view.ExecuteScriptAsync("showView('settings'); element('start-with-windows').click();");
        await Task.Delay(150);
        Check(await view.ExecuteScriptAsync("settings.startWithWindows === true && element('start-with-windows').checked && !element('start-with-windows').disabled") == "true", "设置页未保存开机自启的开启状态");
        await view.ExecuteScriptAsync("element('start-with-windows').click();");
        await Task.Delay(150);
        Check(await view.ExecuteScriptAsync("settings.startWithWindows === false && !element('start-with-windows').checked && !element('start-with-windows').disabled") == "true", "设置页未保存开机自启的关闭状态");
        using (var preview = File.Create(Path.Combine(Program.DataDirectory, "startup-settings-preview.png")))
            await view.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, preview);
        await view.ExecuteScriptAsync("showView('library');");
        report["startupSettings"] = true;
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
              if (action === 'local-list') return structuredClone(localFiles);
              if (action === 'files') return {ticket:'test',directory:'C:\\Downloads\\BOOTH-Shelf',files:[{id:'101',name:'选中的衣装.zip',size:8388608},{id:'102',name:'另一款式.zip',size:1073741824}]};
              if (action === 'product') return {id:payload.id,title:'Original item title',description:'Original description\n第二行原文',images:[],variations:[{name:'Full Set [-30%OFF]'},{name:'Milfy/Eku - (ミルフィ/エク用) [-25%OFF]'},{name:'応援版'}]};
              if (action === 'downloads-start') { globalThis.queuedFiles = payload.ids; return payload.ids.length; }
              return savedRequest(action,payload);
            };
            openFiles(snapshot.items[0]);
            """);
        await Task.Delay(150);
        Check(await view.ExecuteScriptAsync("[...document.querySelectorAll('.file-size')].map(size=>size.textContent).join(',') === '8.0 MB,1.0 GB' && getComputedStyle(document.querySelector('.file-size')).color === 'rgb(200, 50, 50)'") == "true", "文件大小预览或红色字体错误");
        report["fileSizePreview"] = true;
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
        await view.ExecuteScriptAsync("""
            snapshot.items[0].image = 'https://example.test/avatar.jpg';
            localFiles = [{id:'native-local',itemId:'1',fileId:'101',title:snapshot.items[0].title,name:'old.zip'}];
            showView('local');
            document.querySelector('#grid .cover').click();
            element('local-download').click();
            """);
        await Task.Delay(150);
        Check(await view.ExecuteScriptAsync("element('file-dialog').open && !element('local-dialog').open && fileSelection.files.length === 2 && element('file-product').textContent === snapshot.items[0].title") == "true", "本地详情未打开同一资产的补充下载");
        report["additionalLocalDownloads"] = true;
        Check(await view.ExecuteScriptAsync("document.querySelectorAll('.file-downloaded-badge:not([hidden])').length === 1 && document.querySelector('.file-downloaded-badge:not([hidden])').textContent === '已下载' && !document.querySelector('.file-option input').disabled") == "true", "下载窗口未按文件显示已下载状态");
        report["downloadedFileMarker"] = true;
        Check(await view.ExecuteScriptAsync("""
            (() => {
              element('file-dialog').close();
              showView('local');
              document.querySelector('#grid .cover').click();
              element('search').value = 'hidden asset';
              element('local-show-library').click();
              const card = document.querySelector('#grid .card[data-item-id="1"]');
              return currentView === 'library' && !element('local-dialog').open && !element('search').value
                && card.classList.contains('located-asset') && document.activeElement === card;
            })()
            """) == "true", "本地头像未清除筛选并定位到资料库资产");
        report["localCoverLocation"] = true;
        // Keep synthetic menu actions in one turn; hidden-window blur/scroll events dismiss menus between host calls.
        var menuChecks = await view.ExecuteScriptAsync("""
            (() => {
              document.querySelector('#grid .card').dispatchEvent(new MouseEvent('contextmenu',{bubbles:true,cancelable:true,clientX:500,clientY:300}));
              return {open:!element('asset-menu').hidden,label:element('asset-web').textContent,copy:!element('asset-copy').hidden,library:element('asset-library').hidden,folder:element('asset-folder').hidden,delete:element('asset-delete').hidden,target:element('asset-web').target};
            })()
            """);
        var menu = JsonNode.Parse(menuChecks)!;
        Check(menu["open"]!.GetValue<bool>() && menu["label"]!.GetValue<string>() == "跳转网页" && menu["copy"]!.GetValue<bool>()
            && menu["library"]!.GetValue<bool>() && menu["folder"]!.GetValue<bool>() && menu["delete"]!.GetValue<bool>() && menu["target"]!.GetValue<string>() == "_blank",
            "资料库右键菜单内容或外部网页链接错误：" + menuChecks);
        var previousClipboard = Clipboard.GetDataObject();
        try
        {
            await view.ExecuteScriptAsync("document.querySelector('#grid .card').dispatchEvent(new MouseEvent('contextmenu',{bubbles:true,cancelable:true,clientX:500,clientY:300})); element('asset-copy').click();");
            await Task.Delay(150);
            Check(Clipboard.ContainsText() && Clipboard.GetText() == "https://booth.pm/ja/items/1", "商品链接未复制到系统剪贴板");
            await ImageClipboardAsync(view);
        }
        finally { if (previousClipboard != null) Clipboard.SetDataObject(previousClipboard, true); else Clipboard.Clear(); }
        report["libraryContextMenu"] = true;
        report["productUrlClipboard"] = true;
        report["productImageClipboard"] = true;
        await view.ExecuteScriptAsync("element('download-state-filter').value = 'downloaded'; element('download-state-filter').dispatchEvent(new Event('change'));");
        Check(await view.ExecuteScriptAsync("document.querySelectorAll('#grid .card').length === 1 && document.querySelector('#grid .downloaded-badge').textContent === '已下载'") == "true", "已下载标记或筛选错误");
        await view.ExecuteScriptAsync("element('download-state-filter').value = 'not-downloaded'; element('download-state-filter').dispatchEvent(new Event('change'));");
        Check(await view.ExecuteScriptAsync("!document.querySelector('#grid .card[data-item-id=\"1\"]') && document.querySelector('#grid .card[data-item-id=\"2\"]') !== null") == "true", "未下载筛选未排除本地资产");
        await view.ExecuteScriptAsync("element('clear-filters').click(); showView('local'); document.querySelector('#grid .card').dispatchEvent(new MouseEvent('contextmenu',{bubbles:true,cancelable:true,clientX:500,clientY:300}));");
        Check(await view.ExecuteScriptAsync("!element('asset-library').hidden && !element('asset-copy').hidden && !element('asset-folder').hidden && element('asset-folder').textContent === '打开文件目录' && !element('asset-delete').hidden") == "true", "本地菜单缺少复制链接、删除、资料库跳转或文件目录操作");
        await view.ExecuteScriptAsync("document.querySelector('#grid .card').dispatchEvent(new MouseEvent('contextmenu',{bubbles:true,cancelable:true,clientX:500,clientY:300})); element('asset-library').click();");
        Check(await view.ExecuteScriptAsync("currentView === 'library' && document.querySelector('#grid .card[data-item-id=\"1\"]').classList.contains('located-asset')") == "true", "本地右键菜单未定位到资料库");
        await view.ExecuteScriptAsync("document.querySelector('#grid .downloaded-badge').click();");
        Check(await view.ExecuteScriptAsync("currentView === 'local' && document.querySelector('#grid .card[data-item-id=\"1\"]').classList.contains('located-asset') && !element('local-dialog').open") == "true", "已下载标记未定位到本地库");
        await view.ExecuteScriptAsync("locateLibraryItem('1');");
        report["downloadStateFiltering"] = true;
        report["localMenuLibraryLocation"] = true;
        report["downloadedBadgeLocalLocation"] = true;
        await view.ExecuteScriptAsync("document.querySelector('#grid .item-link').click();");
        await Task.Delay(150);
        Check(await view.ExecuteScriptAsync("element('product-dialog').open && !element('product-layout').hidden && element('product-title').textContent === 'Original item title' && element('product-description').textContent === 'Original description\\n第二行原文' && element('product-compatibility').textContent === 'MilfyEku'") == "true", "内置商品详情未保留原文字或排除全套、赞助选项");
        using (var preview = File.Create(Path.Combine(Program.DataDirectory, "product-details-preview.png")))
            await view.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, preview);
        await view.ExecuteScriptAsync("element('product-dialog').close(); desktopRequest = savedRequest; delete globalThis.savedRequest; showView('library');");
        report["embeddedProductDetails"] = true;
        report["fileDialogSelection"] = true;
        report["diagnosticInterface"] = await DiagnosticInterfaceAsync(view);
        return report;
    }

    private static async Task<bool> DiagnosticInterfaceAsync(WebView2 view)
    {
        await view.ExecuteScriptAsync("globalThis.diagnosticLanguage = settings.language;");
        try
        {
            foreach (var language in new[] { "zh-CN", "zh-TW", "ja", "en", "ko" })
            {
                await view.ExecuteScriptAsync($"settings.language = '{language}'; applyLanguage(); showView('settings'); element('open-diagnostics').scrollIntoView();");
                await Task.Delay(80);
                Check(await view.ExecuteScriptAsync("""
                    (() => {
                      const button = element('open-diagnostics'), row = button.closest('.setting-row'), bounds = row.getBoundingClientRect();
                      return button.textContent === t('打开日志目录') && [...row.children].every(node => {
                        const rect = node.getBoundingClientRect();
                        return rect.left >= bounds.left && rect.right <= bounds.right + 1 && node.scrollWidth <= node.clientWidth + 2;
                      });
                    })()
                    """) == "true", "诊断日志设置行溢出：" + language);
                using (var image = File.Create(Path.Combine(Program.DataDirectory, "diagnostics-settings-" + language + ".png")))
                    await view.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, image);
                await view.ExecuteScriptAsync("""
                    showView('library');
                    notify('[BS-SYNC-LIBRARY-FREE-NETWORK] 读取免费商品（第 2 页）：BOOTH 请求超时 · 请检查网络后重试；若仍失败，可在设置中打开诊断日志。', true);
                    window.scrollTo({ top: 0, left: 0, behavior: 'instant' });
                    """);
                await Task.Delay(80);
                Check(await view.ExecuteScriptAsync("element('notice').textContent.includes(t('读取免费商品')) && element('notice').textContent.includes(t('BOOTH 请求超时')) && element('notice').scrollWidth <= element('notice').clientWidth + 2 && element('notice').getBoundingClientRect().top >= 28 && element('notice').getBoundingClientRect().bottom <= innerHeight") == "true", "诊断错误提示未翻译、溢出或被遮挡：" + language);
                using (var image = File.Create(Path.Combine(Program.DataDirectory, "diagnostics-error-" + language + ".png")))
                    await view.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, image);
            }
            return true;
        }
        finally { await view.ExecuteScriptAsync("settings.language = diagnosticLanguage; delete globalThis.diagnosticLanguage; applyLanguage(); showView('library');"); }
    }

    private static async Task ImageClipboardAsync(WebView2 view)
    {
        using var source = new Bitmap(2, 1);
        source.SetPixel(0, 0, Color.Red);
        source.SetPixel(1, 0, Color.Transparent);
        using var png = new MemoryStream();
        source.Save(png, System.Drawing.Imaging.ImageFormat.Png);
        await ShelfWindow.CopyImageToClipboardAsync(view.CoreWebView2, png.ToArray(), ".png");
        using (var copied = Clipboard.GetImage())
            Check(copied?.Width == 2 && copied.Height == 1, "剪贴板中没有实际图片或原始尺寸错误");
        using (var transparent = Clipboard.GetData("PNG") as MemoryStream)
        {
            Check(transparent != null, "剪贴板缺少 PNG 图片内容");
            using var image = new Bitmap(transparent!);
            Check(image.GetPixel(0, 0).ToArgb() == Color.Red.ToArgb() && image.GetPixel(1, 0).A == 0, "复制图片的颜色或透明度丢失");
        }
        var webp = JsonNode.Parse(await view.ExecuteScriptAsync("""
            (() => {
              const canvas = document.createElement('canvas');
              canvas.width = 2; canvas.height = 1;
              const context = canvas.getContext('2d');
              context.fillStyle = '#00ff00'; context.fillRect(0, 0, 2, 1);
              return canvas.toDataURL('image/webp', 1).split(',')[1];
            })()
            """))!.GetValue<string>();
        await ShelfWindow.CopyImageToClipboardAsync(view.CoreWebView2, Convert.FromBase64String(webp), ".webp");
        using (var copied = Clipboard.GetImage())
        {
            Check(copied?.Width == 2 && copied.Height == 1, "WebP 图片未复制为可粘贴的图片");
            using var bitmap = new Bitmap(copied!);
            Check(bitmap.GetPixel(0, 0).G > 240 && bitmap.GetPixel(0, 0).R < 10, "WebP 图片复制后的像素错误");
        }
        var rejected = false;
        try { await ShelfWindow.CopyImageToClipboardAsync(view.CoreWebView2, [1, 2, 3], ".png"); }
        catch (InvalidOperationException) { rejected = true; }
        using var retained = Clipboard.GetImage();
        Check(rejected && retained?.Width == 2, "损坏图片覆盖了剪贴板或未报告失败");
    }
}
