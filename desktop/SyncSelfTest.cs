using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using Microsoft.Web.WebView2.Core;

namespace BoothShelf;

internal static class SyncSelfTest
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("同步回归检查失败：" + message);
    }

    private static LibraryPage Page(int number, int total, string? id = null) => new()
    {
        Page = number, MaxPage = total, DisplayedPage = number,
        Next = number < total ? ShelfWindow.LibraryUrl + "?page=" + (number + 1) : null,
        Items = new JsonArray(new JsonObject
        {
            ["id"] = id ?? number.ToString(), ["title"] = "Asset " + number, ["shop"] = "Test shop",
            ["url"] = "https://artist.booth.pm/items/" + (id ?? number.ToString()),
            ["libraryUrl"] = ShelfWindow.LibraryUrl, ["image"] = ""
        })
    };

    internal static async Task<JsonObject> ReadPagesAsync(CoreWebView2Environment environment)
    {
        using var window = new BoothWindow { Opacity = 0, ShowInTaskbar = false };
        window.Show();
        await window.InitializeAsync(environment);
        var requests = new List<string>();
        var throttled = false;
        window.Browser.CoreWebView2.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        window.Browser.CoreWebView2.WebResourceRequested += (_, args) =>
        {
            var uri = new Uri(args.Request.Uri);
            requests.Add(uri.AbsoluteUri);
            var pageMatch = System.Text.RegularExpressions.Regex.Match(uri.Query, @"(?:[?&])page=(\d+)");
            var number = pageMatch.Success ? int.Parse(pageMatch.Groups[1].Value) : 1;
            var status = 200;
            var headers = "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\n";
            string html;
            if (number == 2 && !throttled)
            {
                throttled = true;
                status = 429;
                headers += "Retry-After: 12\r\n";
                html = "rate limited";
            }
            else if (number == 5)
                html = "<title>Just a moment...</title><main>Verify</main>";
            else if (number == 6)
                html = "<title>Login</title><input type='password'>";
            else
                html = $"<title>Library</title><main><article><a href='https://artist.booth.pm/items/{number}'>Asset {number}</a><h2>Asset {number}</h2><a href='https://artist.booth.pm/'>Test shop</a></article></main><nav class='pagination'><span aria-current='page'>{number}</span><a href='?page=2'>2</a><a href='?page=696'>696</a><a href='?category=other&page=900'>Other category</a></nav>";
            args.Response = environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(html)), status,
                status == 200 ? "OK" : "Too Many Requests", headers);
        };
        await window.NavigateAsync(ShelfWindow.LibraryUrl, CancellationToken.None);
        var first = (await window.ParseAsync(CancellationToken.None)).Deserialize<LibraryPage>(SyncCache.JsonOptions)!;
        Check(first.MaxPage == 696 && first.Items.Count == 1, "真实 DOM 解析页数错误或串入其他筛选条件");
        await window.PrepareReaderAsync();
        window.SetSyncing(true);
        var limited = false;
        try { await window.ReadPageAsync(2, CancellationToken.None); }
        catch (SyncReadException error) { limited = error.Retryable && error.RetryAfterMs == 12000; }
        Check(limited, "HTML 请求未正确传回限流状态");
        var fetched = await Task.WhenAll(new[] { 2, 3, 4 }.Select(number => window.ReadPageAsync(number, CancellationToken.None)));
        Check(fetched.Select(page => page.Page).SequenceEqual(new[] { 2, 3, 4 })
            && fetched.All(page => page.Items.Count == 1 && page.MaxPage == 696), "后台并发 HTML 解析失败");
        foreach (var number in new[] { 5, 6 })
        {
            var stopped = false;
            try { await window.ReadPageAsync(number, CancellationToken.None); }
            catch (SyncReadException error) { stopped = !error.Retryable; }
            Check(stopped, "验证或登录页面没有停止读取");
        }
        Check(window.Browser.Source!.AbsoluteUri == ShelfWindow.LibraryUrl, "后台读取改变了登录窗口地址");
        return new JsonObject { ["htmlFetch"] = true, ["concurrentParsing"] = true, ["http429"] = true, ["loginAndChallenge"] = true };
    }

    internal static async Task<JsonObject> RunAsync(string root)
    {
        Directory.CreateDirectory(root);
        var first = Page(1, 696);
        var cache = SyncCache.Open(Path.Combine(root, "large"), first, "full", "");
        var active = 0;
        var peak = 0;
        var calls = new List<int>();
        async Task<LibraryPage> Read(int number, CancellationToken token)
        {
            var count = Interlocked.Increment(ref active);
            lock (calls) peak = Math.Max(peak, count);
            calls.Add(number);
            try { await Task.Delay(2, token); return Page(number, 696); }
            finally { Interlocked.Decrement(ref active); }
        }
        var unnecessaryWaits = 0;
        var engine = new LibrarySync(Read, (_, _, _, _, _) => { }, (_, _) => { unnecessaryWaits++; return Task.CompletedTask; });
        var complete = await engine.RunAsync(cache, first, null, CancellationToken.None);
        Check(cache.Pages.Count == 696 && complete["items"]!.AsArray().Count == 696, "696 页未完整收录");
        Check(peak == 8 && calls.Count == 696 && unnecessaryWaits == 0, "并发上限、请求次数或固定等待不符");
        Check(complete["syncKind"]!.GetValue<string>() == "full", "完整同步标记不符");
        Check(complete["items"]!.AsArray()[100]!["id"]!.GetValue<string>() == "101", "并发返回顺序未恢复");

        var pauseRoot = Path.Combine(root, "resume");
        var partial = SyncCache.Open(pauseRoot, first, "full", "");
        var interrupted = new LibrarySync((number, _) => number == 104
            ? Task.FromException<LibraryPage>(new SyncReadException("测试登录失效", false))
            : Task.FromResult(Page(number, 696)), (_, _, _, _, _) => { }, (_, _) => Task.CompletedTask);
        try { await interrupted.RunAsync(partial, first, null, CancellationToken.None); throw new Exception("应当暂停"); }
        catch (SyncReadException) { }
        Check(partial.Pages.Count > 100 && !partial.Pages.ContainsKey(104), "中断后缓存不完整");
        var saved = partial.Pages.Keys.ToHashSet();
        var reopened = SyncCache.Open(pauseRoot, first, "full", "");
        var resumedCalls = new List<int>();
        var resumeEngine = new LibrarySync((number, _) =>
        {
            resumedCalls.Add(number);
            return Task.FromResult(Page(number, 696));
        }, (_, _, _, _, _) => { }, (_, _) => Task.CompletedTask);
        await resumeEngine.RunAsync(reopened, first, null, CancellationToken.None);
        Check(reopened.Pages.Count == 696 && resumedCalls.All(number => number == 1 || !saved.Contains(number)), "续传重读了已缓存页面");
        resumedCalls.Clear();
        var ready = SyncCache.Open(pauseRoot, first, "full", "");
        await resumeEngine.RunAsync(ready, first, null, CancellationToken.None);
        Check(resumedCalls.SequenceEqual(new[] { 1 }), "696 页完成缓存没有复用，或额外请求了第 697 页");
        var shifted = SyncCache.Open(pauseRoot, Page(1, 697, "9999"), "full", "");
        Check(shifted.Pages.Count == 1, "首页变化后错误复用分页缓存");

        var quickFirst = Page(1, 697, "9999");
        var quickCache = SyncCache.Open(Path.Combine(root, "quick"), quickFirst, "quick", "baseline");
        var quickCalls = new List<int>();
        var quickEngine = new LibrarySync((number, _) =>
        {
            quickCalls.Add(number);
            return Task.FromResult(number == 1 ? quickFirst : Page(number, 697, (number - 1).ToString()));
        }, (_, _, _, _, _) => { }, (_, _) => Task.CompletedTask);
        var quick = await quickEngine.RunAsync(quickCache, quickFirst, complete, CancellationToken.None);
        Check(quickCalls.SequenceEqual(new[] { 2 }) && quick["items"]!.AsArray().Count == 697, "快速更新没有在最新资产处停止或丢失旧商品");
        Check(quick["syncKind"]!.GetValue<string>() == "quick", "快速模式未明确标记");

        var initialFirst = Page(1, 17);
        var initial = SyncCache.Open(Path.Combine(root, "initial-quick"), initialFirst, "quick", "");
        var initialActive = 0;
        var initialPeak = 0;
        var initialEngine = new LibrarySync(async (number, token) =>
        {
            var count = Interlocked.Increment(ref initialActive);
            lock (calls) initialPeak = Math.Max(initialPeak, count);
            try { await Task.Delay(8, token); return Page(number, 17); }
            finally { Interlocked.Decrement(ref initialActive); }
        }, (_, _, _, _, _) => { }, (_, _) => throw new InvalidOperationException("正常同步不应固定等待"));
        var initialized = await initialEngine.RunAsync(initial, initialFirst, null, CancellationToken.None);
        Check(initialPeak == 8 && initialized["items"]!.AsArray().Count == 17, "首次来源初始化没有并发或发生漏项");

        var retryFirst = Page(1, 9);
        var retryCache = SyncCache.Open(Path.Combine(root, "retry"), retryFirst, "full", "");
        var attempts = new Dictionary<int, int>();
        var waits = new List<int>();
        var retryEngine = new LibrarySync((number, _) =>
        {
            attempts[number] = attempts.GetValueOrDefault(number) + 1;
            return number == 2 && attempts[number] == 1
                ? Task.FromException<LibraryPage>(new SyncReadException("HTTP 429", true, 12000))
                : Task.FromResult(Page(number, 9));
        }, (_, _, _, _, _) => { }, (milliseconds, _) => { waits.Add(milliseconds); return Task.CompletedTask; });
        await retryEngine.RunAsync(retryCache, retryFirst, null, CancellationToken.None);
        Check(attempts[2] == 2 && waits.Contains(12000) && retryCache.Pages.Count == 9, "Retry-After 或重试失败");

        var canceled = SyncCache.Open(Path.Combine(root, "cancel"), first, "full", "");
        using var cancellation = new CancellationTokenSource();
        var cancelEngine = new LibrarySync((number, _) => Task.FromResult(Page(number, 696)),
            (done, _, _, _, _) => { if (done >= 4) cancellation.Cancel(); }, (_, _) => Task.CompletedTask);
        try { await cancelEngine.RunAsync(canceled, first, null, cancellation.Token); throw new Exception("应当取消"); }
        catch (OperationCanceledException) { }
        Check(SyncCache.Load(Path.Combine(root, "cancel"))!.Pages.Count == 9, "暂停没有保存成功页面");

        var moved = SyncCache.Open(Path.Combine(root, "changed"), Page(1, 3), "full", "");
        var changedEngine = new LibrarySync((number, _) => Task.FromResult(number == 1 ? Page(1, 4, "9999") : Page(number, 3)),
            (_, _, _, _, _) => { }, (_, _) => Task.CompletedTask);
        var detected = false;
        try { await changedEngine.RunAsync(moved, Page(1, 3), null, CancellationToken.None); }
        catch (InvalidOperationException) { detected = true; }
        Check(detected && !moved.Finished, "同步期间新增商品未检测");

        var growing = SyncCache.Open(Path.Combine(root, "discover"), Page(1, 2), "full", "");
        var discoverEngine = new LibrarySync((number, _) => Task.FromResult(Page(number, number == 1 ? 2 : Math.Min(120, number + 1))),
            (_, _, _, _, _) => { }, (_, _) => Task.CompletedTask);
        await discoverEngine.RunAsync(growing, Page(1, 2), null, CancellationToken.None);
        Check(growing.Pages.Count == 120, "未逐步发现隐藏的后续分页");

        var repeated = SyncCache.Open(Path.Combine(root, "duplicate"), Page(1, 4), "full", "");
        var duplicateEngine = new LibrarySync((number, _) => Task.FromResult(Page(number, 4, "1")),
            (_, _, _, _, _) => { }, (_, _) => Task.CompletedTask);
        var duplicateStopped = false;
        try { await duplicateEngine.RunAsync(repeated, Page(1, 4), null, CancellationToken.None); }
        catch (InvalidDataException) { duplicateStopped = true; }
        Check(duplicateStopped && repeated.Pages.Count == 1, "重复分页被误当成成功结果");

        return new JsonObject
        {
            ["largeLibraryPages"] = 696, ["concurrency"] = peak, ["resume"] = true, ["completedCacheNoPage697"] = true,
            ["quickRequests"] = quickCalls.Count, ["initialQuickConcurrency"] = initialPeak, ["noFixedDelay"] = true, ["rateLimit"] = true, ["pause"] = true,
            ["changedLibrary"] = true, ["paginationDiscovery"] = true, ["duplicatePage"] = true
        };
    }
}
