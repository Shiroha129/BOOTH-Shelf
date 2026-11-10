using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BoothShelf;

internal sealed class LibraryPage
{
    public int Page { get; set; }
    public int MaxPage { get; set; }
    public int? DisplayedPage { get; set; }
    public string? Next { get; set; }
    public bool Login { get; set; }
    public bool Empty { get; set; }
    public JsonArray Items { get; set; } = new();
    public string Fingerprint => string.Join(",", Items.OfType<JsonObject>().Select(item => item["id"] + ":" + FileFingerprint(item)));
    public string Anchor => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Fingerprint + ":" + MaxPage)));

    private static string FileFingerprint(JsonObject item) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join("\n", (item["files"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
            .OrderBy(file => file["id"]?.GetValue<string>()).Select(file => file["id"] + ":" + file["name"])))));

    public void Validate(int requestedPage)
    {
        if (Login) throw new InvalidOperationException("登录已失效，请重新登录后继续同步。");
        if (Page != requestedPage || (DisplayedPage.HasValue && DisplayedPage != requestedPage))
            throw new InvalidDataException($"第 {requestedPage} 页返回了其他页，已暂停以避免漏项。");
        if ((Items.Count == 0 && !(Empty && requestedPage == 1 && MaxPage <= 1)) || Items.Any(item => string.IsNullOrEmpty(item?["id"]?.GetValue<string>())))
            throw new InvalidDataException($"第 {requestedPage} 页没有识别到商品，请检查 BOOTH 窗口是否需要验证。");
        MaxPage = Math.Max(Page, MaxPage);
    }
}

internal sealed class SyncReadException(string message, bool retryable, int retryAfterMs = 0) : Exception(message)
{
    public bool Retryable { get; } = retryable;
    public int RetryAfterMs { get; } = retryAfterMs;
}

internal sealed class SyncCache
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string manifestPath;
    private readonly string directory;
    private readonly JsonObject manifest;
    internal SortedDictionary<int, LibraryPage> Pages { get; } = new();
    internal string Mode => manifest["mode"]!.GetValue<string>();
    internal bool Finished => manifest["finished"]?.GetValue<bool>() == true;
    internal int TotalPages => Pages.Count == 0 ? 0 : Pages.Values.Max(page => page.MaxPage);

    private SyncCache(string root, JsonObject metadata)
    {
        manifestPath = Path.Combine(root, "sync-checkpoint.json");
        manifest = metadata;
        var runId = metadata["runId"]!.GetValue<string>();
        if (!Guid.TryParseExact(runId, "N", out _)) throw new InvalidDataException("同步缓存标识无效");
        directory = Path.Combine(root, "sync-cache", runId);
        Directory.CreateDirectory(directory);
        foreach (var path in Directory.EnumerateFiles(directory, "page-*.json"))
        {
            var page = JsonSerializer.Deserialize<LibraryPage>(File.ReadAllText(path), JsonOptions)
                ?? throw new InvalidDataException("同步缓存损坏：" + Path.GetFileName(path));
            page.Validate(page.Page);
            Pages[page.Page] = page;
        }
    }

    internal static SyncCache? Load(string root)
    {
        var path = Path.Combine(root, "sync-checkpoint.json");
        if (!File.Exists(path)) return null;
        var manifest = JsonNode.Parse(File.ReadAllText(path))?.AsObject();
        return manifest?["schema"]?.GetValue<int>() == 1 ? new SyncCache(root, manifest) : null;
    }

    internal static SyncCache Open(string root, LibraryPage first, string mode, string baseline)
    {
        var previous = Load(root);
        if (previous != null && !previous.Finished && previous.Mode == mode
            && previous.manifest["anchor"]?.GetValue<string>() == first.Anchor
            && previous.manifest["baseline"]?.GetValue<string>() == baseline)
        {
            previous.Save(first);
            return previous;
        }
        var metadata = new JsonObject
        {
            ["schema"] = 1, ["runId"] = Guid.NewGuid().ToString("N"), ["mode"] = mode,
            ["anchor"] = first.Anchor, ["baseline"] = baseline, ["finished"] = false,
            ["startedAt"] = DateTimeOffset.UtcNow.ToString("O")
        };
        var cache = new SyncCache(root, metadata);
        cache.Save(first);
        AtomicWrite(cache.manifestPath, metadata.ToJsonString());
        return cache;
    }

    internal static void AtomicWrite(string path, string content)
    {
        var temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var bytes = Encoding.UTF8.GetBytes(content);
            stream.Write(bytes);
            stream.Flush(true);
        }
        File.Move(temporary, path, true);
    }

    internal void Save(LibraryPage page)
    {
        page.Validate(page.Page);
        AtomicWrite(Path.Combine(directory, $"page-{page.Page}.json"), JsonSerializer.Serialize(page, JsonOptions));
        Pages[page.Page] = page;
    }

    internal void Finish()
    {
        manifest["finished"] = true;
        AtomicWrite(manifestPath, manifest.ToJsonString());
    }

    internal JsonObject Snapshot(JsonObject? baseline, bool complete, bool quick)
    {
        var items = new Dictionary<string, JsonNode>();
        var older = (baseline?["items"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToDictionary(item => item["id"]!.GetValue<string>());
        var newest = quick ? older.Keys.FirstOrDefault() : null;
        var boundary = false;
        var visited = new List<LibraryPage>();
        foreach (var page in Pages.Values)
        {
            visited.Add(page);
            foreach (var item in page.Items)
            {
                var id = item!["id"]!.GetValue<string>();
                if (id == newest) { boundary = true; break; }
                if (!items.TryGetValue(id, out var current))
                {
                    if (quick && older.TryGetValue(id, out var previous)) current = previous.DeepClone();
                    else
                    {
                        current = new JsonObject();
                        foreach (var field in item.AsObject().Where(field => field.Key != "files")) current[field.Key] = field.Value?.DeepClone();
                    }
                    items[id] = current;
                }
                LibraryOwnership.Merge(current.AsObject(), item.AsObject());
                if (item["files"] is JsonArray files)
                {
                    current["files"] ??= new JsonArray();
                    var existing = current["files"]!.AsArray();
                    var seen = existing.Select(file => file!["id"]!.GetValue<string>()).ToHashSet();
                    foreach (var file in files)
                        if (seen.Add(file!["id"]!.GetValue<string>())) existing.Add(file.DeepClone());
                }
            }
            if (boundary) break;
        }
        if (quick)
            foreach (var item in older) items.TryAdd(item.Key, item.Value.DeepClone());
        return new JsonObject
        {
            ["items"] = new JsonArray(items.Values.ToArray()),
            ["pages"] = new JsonArray(visited.Select(page => (JsonNode)new JsonObject
            {
                ["page"] = page.Page, ["url"] = ShelfWindow.LibraryUrl + "?page=" + page.Page, ["count"] = page.Items.Count
            }).ToArray()),
            ["syncedAt"] = complete ? DateTimeOffset.UtcNow.ToString("O") : null,
            ["fullSyncedAt"] = complete && !quick ? DateTimeOffset.UtcNow.ToString("O") : baseline?["fullSyncedAt"]?.DeepClone(),
            ["totalPages"] = TotalPages, ["complete"] = complete, ["syncKind"] = quick ? "quick" : "full"
        };
    }
}

internal sealed class LibrarySync(
    Func<int, CancellationToken, Task<LibraryPage>> read,
    Action<int, int, int, string, double?> progress,
    Func<int, CancellationToken, Task>? delay = null)
{
    private readonly Func<int, CancellationToken, Task> wait = delay ?? ((milliseconds, token) => Task.Delay(milliseconds, token));

    internal async Task<JsonObject> RunAsync(SyncCache cache, LibraryPage first, JsonObject? baseline, CancellationToken token)
    {
        var newest = baseline?["items"]?.AsArray().FirstOrDefault()?["id"]?.GetValue<string>();
        var fingerprints = new Dictionary<string, int>();
        foreach (var page in cache.Pages.Values) fingerprints.TryAdd(page.Fingerprint, page.Page);
        var failures = new Dictionary<int, int>();
        var watch = Stopwatch.StartNew();
        var downloaded = 0;
        var concurrency = cache.Mode == "quick" && newest != null ? 1 : 8;
        var ids = cache.Pages.Values.SelectMany(page => page.Items).Select(item => item!["id"]!.GetValue<string>()).ToHashSet();
        void Report(string status)
        {
            double? seconds = cache.Mode == "full" && downloaded > 0 ? (cache.TotalPages - cache.Pages.Count) * watch.Elapsed.TotalSeconds / downloaded : null;
            progress(cache.Pages.Count, cache.TotalPages, ids.Count, status, seconds);
        }
        bool QuickBoundary()
        {
            if (cache.Mode != "quick" || newest == null) return false;
            for (var number = 1; cache.Pages.TryGetValue(number, out var page); number++)
                if (page.Items.Any(item => item!["id"]?.GetValue<string>() == newest)) return true;
            return false;
        }
        Report(cache.Pages.Count > 1 ? "已恢复上次保存的进度" : "后台读取中");
        while (!QuickBoundary())
        {
            token.ThrowIfCancellationRequested();
            var batch = new List<int>();
            var total = cache.TotalPages;
            for (var number = 1; number <= total && batch.Count < concurrency; number++)
                if (!cache.Pages.ContainsKey(number)) batch.Add(number);
            if (batch.Count == 0) break;
            var results = await Task.WhenAll(batch.Select(async number =>
            {
                try { return (Number: number, Page: await read(number, token), Error: (Exception?)null); }
                catch (Exception error) { return (Number: number, Page: (LibraryPage?)null, Error: error); }
            }));
            Exception? fatal = null;
            var backoff = 0;
            foreach (var result in results)
            {
                if (result.Error is SyncReadException { Retryable: true } retry)
                {
                    var attempt = failures.GetValueOrDefault(result.Number) + 1;
                    failures[result.Number] = attempt;
                    if (attempt >= 4) fatal ??= new IOException($"第 {result.Number} 页多次读取失败：{retry.Message}");
                    else backoff = Math.Max(backoff, Math.Max(retry.RetryAfterMs, 5000 * (1 << (attempt - 1))));
                    continue;
                }
                if (result.Error != null) { fatal ??= result.Error; continue; }
                try
                {
                    var page = result.Page!;
                    page.Validate(result.Number);
                    if (fingerprints.TryGetValue(page.Fingerprint, out var other) && other != page.Page)
                        throw new InvalidDataException($"第 {page.Page} 页重复返回第 {other} 页内容，已暂停以避免漏项。");
                    cache.Save(page);
                    fingerprints[page.Fingerprint] = page.Page;
                    foreach (var item in page.Items) ids.Add(item!["id"]!.GetValue<string>());
                    downloaded++;
                    Report("后台读取中 · 已保存进度");
                }
                catch (Exception error) { fatal ??= error; }
            }
            if (fatal != null) throw fatal;
            token.ThrowIfCancellationRequested();
            if (backoff > 0)
            {
                concurrency = 1;
                Report($"网站限流或网络波动，等待 {Math.Ceiling(backoff / 1000d)} 秒后以单路重试");
                await wait(backoff, token);
            }
        }
        token.ThrowIfCancellationRequested();
        if (cache.Mode != "quick" && cache.Pages.Count > 1)
        {
            Report("核对首页，确认同步期间库未移动");
            var current = await read(1, token);
            current.Validate(1);
            if (current.Anchor != first.Anchor)
                throw new InvalidOperationException("同步期间 Library 首页或页数发生变化。进度已保留；请再次同步，程序会重建分页以避免漏项。");
        }
        var quick = cache.Mode == "quick";
        return cache.Snapshot(baseline, true, quick);
    }
}
