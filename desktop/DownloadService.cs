using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace BoothShelf;

internal sealed class SessionBrowser(BoothRequestPolicy? access = null) : IDisposable
{
    private sealed class BackgroundWindow : Form
    {
        protected override bool ShowWithoutActivation => true;
    }

    internal readonly Form Window = new BackgroundWindow { Width = 1050, Height = 850, Opacity = 0, ShowInTaskbar = false };
    internal readonly WebView2 View = new() { Dock = DockStyle.Fill };

    internal async Task InitializeAsync(CoreWebView2Environment environment)
    {
        Window.Controls.Add(View);
        Window.Show();
        await View.EnsureCoreWebView2Async(environment).WaitAsync(TimeSpan.FromSeconds(30));
        if (View.CoreWebView2 == null) throw new InvalidOperationException("WebView2 未能初始化，请关闭程序后重试。");
        View.CoreWebView2.Settings.AreHostObjectsAllowed = false;
        View.CoreWebView2.Settings.IsWebMessageEnabled = false;
        View.CoreWebView2.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
        View.CoreWebView2.NewWindowRequested += (_, args) => args.Handled = true;
    }

    internal async Task NavigateAsync(string url, CancellationToken token)
    {
        if (access == null) await NavigateCoreAsync(url, token);
        else await access.RunAsync(() => NavigateCoreAsync(url, token), token);
    }

    private async Task<bool> NavigateCoreAsync(string url, CancellationToken token)
    {
        var completion = new TaskCompletionSource<bool>();
        void Completed(object? sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            try { access?.Inspect(args.HttpStatusCode); }
            catch (Exception error) { completion.TrySetException(error); return; }
            if (!args.IsSuccess || args.HttpStatusCode >= 400) completion.TrySetException(new IOException($"BOOTH 页面加载失败（HTTP {args.HttpStatusCode}）。请重新登录或稍后重试。"));
            else completion.TrySetResult(true);
        }
        View.CoreWebView2.NavigationCompleted += Completed;
        try
        {
            View.CoreWebView2.Navigate(url);
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(45), token);
            if (access != null) access.Inspect(200, await View.ExecuteScriptAsync(BoothRequestPolicy.ChallengeScript) == "true");
            return true;
        }
        finally { View.CoreWebView2.NavigationCompleted -= Completed; }
    }

    public void Dispose() { View.Dispose(); Window.Dispose(); }
}

internal sealed class DownloadService : IDisposable
{
    private readonly string historyPath;
    private readonly CancellationToken lifetime;
    private readonly Func<Task<CoreWebView2Environment>> environment;
    private readonly Func<string> downloadDirectory;
    private readonly Action<SessionBrowser>? configureSession;
    private readonly BoothRequestPolicy? access;
    private readonly Dictionary<string, Selection> selections = new();
    private readonly Queue<(JsonObject Record, string Url, string Directory)> queue = new();
    private readonly JsonArray history;
    private readonly SemaphoreSlim listingLock = new(1, 1);
    private SessionBrowser? listing;
    private SessionBrowser? downloader;
    private CancellationTokenSource? currentCancellation;
    private string? currentId;
    private bool working;
    private PendingDownload? pending;
    internal event Action<object>? Changed;
    internal event Action<JsonObject>? Completed;
    internal JsonArray History => (JsonArray)history.DeepClone();
    internal bool Busy => working;
    internal bool HasActiveItem(string itemId) => history.Any(record => record?["itemId"]?.GetValue<string>() == itemId
        && record?["state"]?.GetValue<string>() is "queued" or "downloading");

    private sealed record Selection(string ItemId, string Title, JsonArray Files, DateTimeOffset Created, string Image, string Shop);
    private sealed class PendingDownload(JsonObject record, string partial, TaskCompletionSource<CoreWebView2DownloadOperation> started)
    {
        internal JsonObject Record = record;
        internal string Partial = partial;
        internal TaskCompletionSource<CoreWebView2DownloadOperation> Started = started;
        internal CoreWebView2DownloadOperation? Operation;
    }

    internal DownloadService(string root, Func<Task<CoreWebView2Environment>> environment, Func<string> directory, CancellationToken token,
        Action<SessionBrowser>? configureSession = null, BoothRequestPolicy? access = null)
    {
        this.access = access;
        historyPath = Path.Combine(root, "downloads.json");
        this.environment = environment;
        downloadDirectory = directory;
        this.configureSession = configureSession;
        lifetime = token;
        history = File.Exists(historyPath) ? JsonNode.Parse(File.ReadAllText(historyPath))?.AsArray() ?? new JsonArray() : new JsonArray();
        foreach (var record in history.OfType<JsonObject>())
            if (record["state"]?.GetValue<string>() is "queued" or "downloading") record["state"] = "interrupted";
    }

    internal static string SafeName(string value)
    {
        var cleaned = new string(value.Select(character => Path.GetInvalidFileNameChars().Contains(character) || char.IsControl(character) ? '_' : character).ToArray()).Trim().TrimEnd('.');
        if (cleaned.Length > 100) cleaned = cleaned[..85] + Path.GetExtension(cleaned)[..Math.Min(12, Path.GetExtension(cleaned).Length)];
        if (string.IsNullOrWhiteSpace(cleaned)) cleaned = "download";
        if (Regex.IsMatch(cleaned, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", RegexOptions.IgnoreCase)) cleaned = "_" + cleaned;
        return cleaned;
    }

    internal static string UniquePath(string directory, string filename)
    {
        Directory.CreateDirectory(directory);
        var safe = SafeName(filename);
        var path = Path.Combine(directory, safe);
        for (var suffix = 2; File.Exists(path) || File.Exists(path + ".part"); suffix++)
            path = Path.Combine(directory, Path.GetFileNameWithoutExtension(safe) + $" ({suffix})" + Path.GetExtension(safe));
        return path;
    }

    internal async Task<JsonObject> ListAsync(JsonObject item)
    {
        await listingLock.WaitAsync(lifetime);
        try
        {
            var id = item["id"]!.GetValue<string>();
            if (item["files"] is JsonArray { Count: > 0 } cachedFiles)
            {
                var available = new JsonArray(cachedFiles.OfType<JsonObject>().Select(file =>
                {
                    var fileId = file["id"]!.GetValue<string>();
                    if (!Regex.IsMatch(fileId, @"^\d{1,20}$")) throw new InvalidDataException("文件 ID 无效");
                    var copy = (JsonObject)file.DeepClone();
                    copy["url"] = "https://booth.pm/downloadables/" + fileId;
                    return (JsonNode)copy;
                }).ToArray());
                return SelectFiles(item, available);
            }
            if (listing == null)
            {
                var pendingSession = new SessionBrowser(access);
                try
                {
                    await pendingSession.InitializeAsync(await environment());
                    configureSession?.Invoke(pendingSession);
                    pendingSession.View.CoreWebView2.DownloadStarting += (_, args) => args.Cancel = true;
                    listing = pendingSession;
                }
                catch { pendingSession.Dispose(); throw; }
            }
            var source = item["libraryUrl"]?.GetValue<string>() ?? ShelfWindow.LibraryUrl;
            if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "accounts.booth.pm"
                || !Regex.IsMatch(uri.AbsolutePath, @"^/library(?:/[^/]+)?/?$")) source = ShelfWindow.LibraryUrl;
            await listing.NavigateAsync(source, lifetime);
            var current = listing.View.Source;
            if (current?.Host != "accounts.booth.pm" || !current.AbsolutePath.StartsWith("/library"))
                throw new InvalidOperationException("登录已失效，请在设置中重新登录 BOOTH。");
            var files = new JsonArray();
            for (var attempt = 0; attempt < 8; attempt++)
            {
                var json = await listing.View.ExecuteScriptAsync(Program.ReadAsset("parser.js")
                    + "\n({login: BoothParser.parse(document, location.href).login, files: BoothParser.files(document, location.href, " + JsonSerializer.Serialize(id) + ")});");
                var parsed = JsonNode.Parse(json);
                if (parsed?["login"]?.GetValue<bool>() == true)
                    throw new InvalidOperationException("登录已失效，请在设置中重新登录 BOOTH。");
                files = parsed?["files"]?.DeepClone().AsArray() ?? new JsonArray();
                if (files.Count > 0) break;
                await Task.Delay(350, lifetime);
            }
            if (files.Count == 0) throw new InvalidOperationException("当前 Library 页未找到可下载文件。请先快速更新；若商品位置已变化，请全局更新后重试。");
            foreach (var entry in files)
            {
                var url = new Uri(entry!["url"]!.GetValue<string>());
                if (url.Scheme != "https" || url.Host != "booth.pm" || !Regex.IsMatch(url.AbsolutePath, @"^/downloadables/\d+/?$"))
                    throw new InvalidDataException("文件地址不属于 BOOTH 下载入口。");
            }
            return SelectFiles(item, files);
        }
        finally { listingLock.Release(); }
    }

    private JsonObject SelectFiles(JsonObject item, JsonArray files)
    {
        foreach (var expired in selections.Where(pair => DateTimeOffset.UtcNow - pair.Value.Created > TimeSpan.FromMinutes(20)).Select(pair => pair.Key).ToArray()) selections.Remove(expired);
        var ticket = Guid.NewGuid().ToString("N");
        var sorted = new JsonArray(files.OfType<JsonObject>().OrderBy(FilePriority).Select(file => (JsonNode)file.DeepClone()).ToArray());
        selections[ticket] = new Selection(item["id"]!.GetValue<string>(), item["title"]!.GetValue<string>(), sorted, DateTimeOffset.UtcNow,
            item["image"]?.GetValue<string>() ?? "", item["shop"]?.GetValue<string>() ?? "");
        return new JsonObject { ["ticket"] = ticket, ["title"] = item["title"]!.DeepClone(), ["directory"] = downloadDirectory(),
            ["files"] = new JsonArray(sorted.Select(file => (JsonNode)new JsonObject { ["id"] = file!["id"]!.DeepClone(),
                ["name"] = file["name"]!.DeepClone(), ["material"] = file["material"]?.DeepClone() }).ToArray()) };
    }

    private static int FilePriority(JsonObject file)
    {
        if (file["material"]?.GetValue<bool>() == true) return 0;
        var name = (file["name"]?.GetValue<string>() ?? "").Normalize(System.Text.NormalizationForm.FormKC);
        return Regex.IsMatch(name, "milfy|ミルフィ|みるふぃ|エク|えく|eku", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ? 1 : 2;
    }

    internal int Enqueue(string ticket, string[] fileIds)
    {
        if (!selections.TryGetValue(ticket, out var selection) || DateTimeOffset.UtcNow - selection.Created > TimeSpan.FromMinutes(20))
            throw new InvalidOperationException("文件列表已过期，请重新打开下载窗口。");
        var chosen = fileIds.Distinct().ToArray();
        if (chosen.Length == 0) throw new InvalidOperationException("请先选择要下载的文件。");
        var files = chosen.Select(id => selection.Files.FirstOrDefault(file => file?["id"]?.GetValue<string>() == id)
            ?? throw new InvalidDataException("文件不在当前商品的可下载列表中。")).ToArray();
        var directory = Path.GetFullPath(downloadDirectory());
        foreach (var file in files)
        {
            var record = new JsonObject
            {
                ["id"] = Guid.NewGuid().ToString("N"), ["itemId"] = selection.ItemId, ["title"] = selection.Title,
                ["image"] = selection.Image, ["shop"] = selection.Shop,
                ["name"] = file["name"]!.DeepClone(), ["state"] = "queued", ["bytes"] = 0L,
                ["createdAt"] = DateTimeOffset.UtcNow.ToString("O")
            };
            history.Insert(0, record);
            queue.Enqueue((record, file["url"]!.GetValue<string>(), directory));
        }
        Save();
        Emit();
        if (!working) _ = RunAsync();
        return files.Length;
    }

    internal bool Cancel(string id)
    {
        var record = history.OfType<JsonObject>().FirstOrDefault(item => item["id"]?.GetValue<string>() == id);
        if (record?["state"]?.GetValue<string>() == "queued") record["state"] = "canceled";
        if (currentId == id) currentCancellation?.Cancel();
        Save();
        Emit();
        return true;
    }

    private void Save()
    {
        while (history.Count > 500 && history.Last()?["state"]?.GetValue<string>() is not ("queued" or "downloading")) history.RemoveAt(history.Count - 1);
        SyncCache.AtomicWrite(historyPath, history.ToJsonString());
    }

    private void Emit() => Changed?.Invoke(new { type = "downloads", items = History });

    internal void ResumePending()
    {
        if (!working && queue.Count > 0) _ = RunAsync();
    }

    private async Task RunAsync()
    {
        working = true;
        try
        {
            while (queue.TryDequeue(out var entry))
            {
                var record = entry.Record;
                if (record["state"]?.GetValue<string>() != "queued") continue;
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
                currentCancellation = cancellation;
                currentId = record["id"]!.GetValue<string>();
                try
                {
                    access?.ThrowIfPaused();
                    if (downloader == null)
                    {
                        downloader = new SessionBrowser(access);
                        await downloader.InitializeAsync(await environment());
                        configureSession?.Invoke(downloader);
                        downloader.View.CoreWebView2.DownloadStarting += (_, args) =>
                        {
                            if (pending == null || pending.Operation != null || lifetime.IsCancellationRequested)
                            { args.Cancel = true; return; }
                            args.Handled = true;
                            args.ResultFilePath = pending.Partial;
                            pending.Operation = args.DownloadOperation;
                            pending.Started.TrySetResult(args.DownloadOperation);
                        };
                        downloader.View.CoreWebView2.NavigationCompleted += async (_, args) =>
                        {
                            var current = pending;
                            if (current == null || current.Started.Task.IsCompleted) return;
                            try
                            {
                                access?.Inspect(args.HttpStatusCode);
                                if (access != null && args.IsSuccess)
                                    access.Inspect(args.HttpStatusCode, await downloader.View.ExecuteScriptAsync(BoothRequestPolicy.ChallengeScript) == "true");
                            }
                            catch (Exception error) { current.Started.TrySetException(error); return; }
                            if (args.HttpStatusCode >= 400)
                                current.Started.TrySetException(new IOException($"BOOTH 下载入口返回 HTTP {args.HttpStatusCode}，请重新登录或稍后重试。"));
                            else if (args.IsSuccess)
                                current.Started.TrySetException(new IOException("BOOTH 返回了网页，未返回下载文件。请在设置中重新登录，或打开商品页确认下载权限。"));
                        };
                    }
                    var directory = Path.Combine(entry.Directory, SafeName(record["itemId"]!.GetValue<string>() + "_" + record["title"]!.GetValue<string>()));
                    var target = UniquePath(directory, record["name"]!.GetValue<string>());
                    record["path"] = target;
                    record["assetDirectory"] = directory;
                    record["downloadRoot"] = entry.Directory;
                    record["state"] = "downloading";
                    Save();
                    Emit();
                    var started = new TaskCompletionSource<CoreWebView2DownloadOperation>();
                    pending = new PendingDownload(record, target + ".part", started);
                    async Task<CoreWebView2DownloadOperation> Start()
                    {
                        downloader.View.CoreWebView2.Navigate(entry.Url);
                        return await started.Task.WaitAsync(TimeSpan.FromSeconds(45), cancellation.Token);
                    }
                    var operation = await (access == null ? Start() : access.RunAsync(Start, cancellation.Token));
                    var completed = new TaskCompletionSource<bool>();
                    var lastUpdate = Stopwatch.StartNew();
                    void Progress(object? sender, object args)
                    {
                        record["bytes"] = operation.BytesReceived;
                        record["total"] = operation.TotalBytesToReceive;
                        if (lastUpdate.ElapsedMilliseconds > 350) { Emit(); lastUpdate.Restart(); }
                    }
                    void StateChanged(object? sender, object args)
                    {
                        if (operation.State == CoreWebView2DownloadState.Completed) completed.TrySetResult(true);
                        if (operation.State == CoreWebView2DownloadState.Interrupted)
                            completed.TrySetException(new IOException("下载中断：" + operation.InterruptReason));
                    }
                    operation.BytesReceivedChanged += Progress;
                    operation.StateChanged += StateChanged;
                    try
                    {
                        StateChanged(null, EventArgs.Empty);
                        await completed.Task.WaitAsync(cancellation.Token);
                        record["bytes"] = operation.BytesReceived;
                        record["total"] = operation.TotalBytesToReceive;
                        File.Move(target + ".part", target, false);
                        record["state"] = "completed";
                        Completed?.Invoke((JsonObject)record.DeepClone());
                    }
                    finally
                    {
                        operation.BytesReceivedChanged -= Progress;
                        operation.StateChanged -= StateChanged;
                        if (operation.State == CoreWebView2DownloadState.InProgress) operation.Cancel();
                    }
                }
                catch (BoothAccessPausedException)
                {
                    record["state"] = "queued";
                    queue.Enqueue(entry);
                }
                catch (OperationCanceledException) { record["state"] = "canceled"; }
                catch (Exception error)
                {
                    record["state"] = "failed";
                    record["error"] = error is TimeoutException ? "未开始下载，请在设置中重新登录后重试。" : error.Message;
                }
                finally
                {
                    pending?.Operation?.CancelIfInProgress();
                    if (pending?.Operation == null && downloader != null)
                    {
                        downloader.Dispose();
                        downloader = null;
                    }
                    pending = null;
                    currentCancellation = null;
                    currentId = null;
                    Save();
                    Emit();
                }
                if (lifetime.IsCancellationRequested || access?.Paused == true) break;
            }
        }
        catch (Exception error)
        {
            foreach (var record in history.OfType<JsonObject>().Where(item => item["state"]?.GetValue<string>() == "queued"))
            { record["state"] = "failed"; record["error"] = error.Message; }
            Save();
            Emit();
        }
        finally { working = false; }
    }

    public void Dispose()
    {
        currentCancellation?.Cancel();
        pending?.Operation?.CancelIfInProgress();
        listing?.Dispose();
        downloader?.Dispose();
    }
}

internal static class DownloadOperationExtensions
{
    internal static void CancelIfInProgress(this CoreWebView2DownloadOperation operation)
    {
        if (operation.State == CoreWebView2DownloadState.InProgress) operation.Cancel();
    }
}
