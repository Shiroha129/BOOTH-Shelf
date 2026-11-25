using System.Diagnostics;
using System.Net.Http;
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

        protected override CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;
                parameters.ExStyle |= 0x80; // WS_EX_TOOLWINDOW keeps background browsers out of Alt+Tab.
                return parameters;
            }
        }
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
    private readonly HttpClient sizeClient;
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
        Action<SessionBrowser>? configureSession = null, BoothRequestPolicy? access = null, HttpMessageHandler? sizeHandler = null)
    {
        this.access = access;
        sizeClient = sizeHandler == null ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) : new HttpClient(sizeHandler);
        sizeClient.Timeout = TimeSpan.FromSeconds(15);
        sizeClient.DefaultRequestHeaders.UserAgent.ParseAdd("BOOTH-Shelf/" + Program.AppVersion.ToString(3));
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

    internal async Task<JsonObject> ListAsync(JsonObject item, bool refresh = false)
    {
        await listingLock.WaitAsync(lifetime);
        try
        {
            var id = item["id"]!.GetValue<string>();
            if (!refresh && item["files"] is JsonArray { Count: > 0 } cachedFiles)
            {
                var available = new JsonArray(cachedFiles.OfType<JsonObject>().Select(file =>
                {
                    var fileId = file["id"]!.GetValue<string>();
                    if (!Regex.IsMatch(fileId, @"^\d{1,20}$")) throw new InvalidDataException("文件 ID 无效");
                    var copy = (JsonObject)file.DeepClone();
                    copy["url"] = "https://booth.pm/downloadables/" + fileId;
                    return (JsonNode)copy;
                }).ToArray());
                return await SelectFilesAsync(item, available);
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
            return await SelectFilesAsync(item, files);
        }
        finally { listingLock.Release(); }
    }

    private async Task FillFileSizesAsync(string id, JsonArray files)
    {
        if (files.OfType<JsonObject>().All(file => file["size"] != null)) return;
        JsonArray variants;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.booth.pm/vroid/items/" + id);
            using var response = await (access == null ? sizeClient.SendAsync(request, lifetime) : access.SendAsync(sizeClient, request, lifetime));
            response.EnsureSuccessStatusCode();
            var data = JsonNode.Parse(await response.Content.ReadAsStringAsync(lifetime));
            if (data?["id"]?.ToString() != id || data["variations"] is not JsonArray values) return;
            variants = values;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException or InvalidOperationException or TaskCanceledException) { return; }
        foreach (var file in files.OfType<JsonObject>().Where(file => file["size"] == null))
        {
            var owned = (file["variationIds"] as JsonArray ?? new JsonArray()).Select(value => value!.ToString()).ToArray();
            var matches = variants.OfType<JsonObject>().Where(variant => owned.Length == 0 || owned.Contains(variant["id"]?.ToString()))
                .SelectMany(variant => (variant["downloadables"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                .Where(candidate => candidate["name"]?.ToString() == file["name"]?.ToString() && candidate["file_size"] != null)
                .Select(candidate => candidate["file_size"]!).GroupBy(size => size.ToJsonString()).ToArray();
            if (matches.Length == 1) file["size"] = matches[0].First().DeepClone();
        }
    }

    private async Task<JsonObject> SelectFilesAsync(JsonObject item, JsonArray files)
    {
        await FillFileSizesAsync(item["id"]!.GetValue<string>(), files);
        foreach (var expired in selections.Where(pair => DateTimeOffset.UtcNow - pair.Value.Created > TimeSpan.FromMinutes(20)).Select(pair => pair.Key).ToArray()) selections.Remove(expired);
        var ticket = Guid.NewGuid().ToString("N");
        var sorted = new JsonArray(files.OfType<JsonObject>().OrderBy(FilePriority).Select(file => (JsonNode)file.DeepClone()).ToArray());
        selections[ticket] = new Selection(item["id"]!.GetValue<string>(), item["title"]!.GetValue<string>(), sorted, DateTimeOffset.UtcNow,
            item["image"]?.GetValue<string>() ?? "", item["shop"]?.GetValue<string>() ?? "");
        return new JsonObject { ["ticket"] = ticket, ["title"] = item["title"]!.DeepClone(), ["directory"] = downloadDirectory(),
            ["files"] = new JsonArray(sorted.Select(file => (JsonNode)new JsonObject { ["id"] = file!["id"]!.DeepClone(),
                ["name"] = file["name"]!.DeepClone(), ["material"] = file["material"]?.DeepClone(), ["size"] = file["size"]?.DeepClone() }).ToArray()) };
    }

    private static int FilePriority(JsonObject file)
    {
        if (file["material"]?.GetValue<bool>() == true) return 0;
        var name = (file["name"]?.GetValue<string>() ?? "").Normalize(System.Text.NormalizationForm.FormKC);
        return Regex.IsMatch(name, "milfy|ミルフィ|みるふぃ|エク|えく|eku", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ? 1 : 2;
    }

    private Selection GetSelection(string ticket)
    {
        if (!selections.TryGetValue(ticket, out var selection) || DateTimeOffset.UtcNow - selection.Created > TimeSpan.FromMinutes(20))
            throw new InvalidOperationException("文件列表已过期，请重新打开下载窗口。");
        return selection;
    }

    internal int EnqueueUpdate(string ticket, string[] names, JsonObject localFile)
    {
        access?.ThrowIfPaused();
        var selection = GetSelection(ticket);
        if (HasActiveItem(selection.ItemId)) throw new InvalidOperationException("该资产正在下载，请在下载页查看。");
        var ids = names.Distinct().Select(name =>
        {
            var matches = selection.Files.OfType<JsonObject>().Where(file => file["name"]?.GetValue<string>() == name)
                .Select(file => file["id"]!.GetValue<string>()).Distinct().ToArray();
            if (matches.Length != 1) throw new InvalidOperationException("更新文件已变化，请刷新资料库后重试。");
            return matches[0];
        }).ToArray();
        return Enqueue(ticket, ids, localFile);
    }

    internal int Enqueue(string ticket, string[] fileIds, JsonObject? localFile = null)
    {
        var selection = GetSelection(ticket);
        var chosen = fileIds.Distinct().ToArray();
        if (chosen.Length == 0) throw new InvalidOperationException("请先选择要下载的文件。");
        var files = chosen.Select(id => selection.Files.FirstOrDefault(file => file?["id"]?.GetValue<string>() == id)
            ?? throw new InvalidDataException("文件不在当前商品的可下载列表中。")).ToArray();
        var title = selection.Title;
        string directory;
        if (localFile == null) directory = Path.GetFullPath(downloadDirectory());
        else
        {
            if (localFile["itemId"]?.GetValue<string>() != selection.ItemId)
                throw new InvalidOperationException("无法确认资产文件夹，请使用打开所在目录手动处理。");
            var target = localFile["path"]?.GetValue<string>();
            if (!File.Exists(target)) throw new FileNotFoundException("该资产没有可用的本地文件");
            var assetDirectory = Path.GetDirectoryName(Path.GetFullPath(target!))!;
            title = localFile["title"]?.GetValue<string>() ?? "";
            if (string.IsNullOrWhiteSpace(title) || !string.Equals(Path.GetFileName(assetDirectory), SafeName(selection.ItemId + "_" + title), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("无法确认资产文件夹，请使用打开所在目录手动处理。");
            directory = Path.GetDirectoryName(assetDirectory)!;
        }
        foreach (var file in files)
        {
            var record = new JsonObject
            {
                ["id"] = Guid.NewGuid().ToString("N"), ["itemId"] = selection.ItemId, ["title"] = title,
                ["fileId"] = file["id"]!.DeepClone(),
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

    internal string GetFolder(string id)
    {
        var record = history.OfType<JsonObject>().FirstOrDefault(record => record["id"]?.GetValue<string>() == id)
            ?? throw new InvalidOperationException("下载记录不存在。");
        var target = record["path"]?.GetValue<string>() ?? throw new InvalidOperationException("下载文件尚未保存。");
        var directory = Path.GetDirectoryName(Path.GetFullPath(target))!;
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("下载目录已移动或删除。");
        return directory;
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
                    Program.Diagnostics?.Record("download.transfer", "started");
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
                        Program.Diagnostics?.Record("download.transfer", "completed");
                        Completed?.Invoke((JsonObject)record.DeepClone());
                    }
                    finally
                    {
                        operation.BytesReceivedChanged -= Progress;
                        operation.StateChanged -= StateChanged;
                        if (operation.State == CoreWebView2DownloadState.InProgress) operation.Cancel();
                    }
                }
                catch (BoothAccessPausedException error)
                {
                    Program.Diagnostics?.Record("download.transfer", "paused", DiagnosticFailure.CodeFor("download.transfer", error), error);
                    record["state"] = "queued";
                    queue.Enqueue(entry);
                }
                catch (OperationCanceledException) { record["state"] = "canceled"; }
                catch (Exception error)
                {
                    record["state"] = "failed";
                    record["error"] = DiagnosticFailure.Capture("download.transfer", error,
                        error is TimeoutException ? "未开始下载，请在设置中重新登录后重试。" : null).Message;
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
            var failure = DiagnosticFailure.Capture("download.prepare", error);
            foreach (var record in history.OfType<JsonObject>().Where(item => item["state"]?.GetValue<string>() == "queued"))
            { record["state"] = "failed"; record["error"] = failure.Message; }
            Save();
            Emit();
        }
        finally { working = false; }
    }

    public void Dispose()
    {
        sizeClient.Dispose();
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
