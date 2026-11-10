using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace BoothShelf;

internal static class Program
{
    internal static string DataDirectory = "";
    internal static bool SmokeTest;

    [STAThread]
    private static void Main(string[] args)
    {
        SmokeTest = args.Contains("--smoke-test");
        var dataIndex = Array.IndexOf(args, "--data-dir");
        if (args.Any(argument => argument is "--smoke-test" or "--core-test" or "--session-test" or "--session-verify" or "--tray-test")
            && (dataIndex < 0 || dataIndex + 1 >= args.Length || string.IsNullOrWhiteSpace(args[dataIndex + 1]) || args[dataIndex + 1].StartsWith("--")))
        { Environment.ExitCode = 2; return; }
        DataDirectory = dataIndex >= 0 && args.Length > dataIndex + 1
            ? Path.GetFullPath(args[dataIndex + 1])
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BOOTH-Shelf");
        Directory.CreateDirectory(DataDirectory);
        NativeLanguage.Initialize(DataDirectory);
        if (args.Contains("--core-test"))
        {
            CoreSelfTest.RunAsync(DataDirectory).GetAwaiter().GetResult();
            return;
        }
        if (args.Contains("--session-test"))
        {
            SessionSelfTest.Run(DataDirectory);
            return;
        }
        if (args.Contains("--session-verify"))
        {
            SessionSelfTest.VerifyPersistedFixture(DataDirectory);
            return;
        }
        ApplicationConfiguration.Initialize();
        if (args.Contains("--tray-test"))
        {
            TraySelfTest.Run(DataDirectory);
            return;
        }
        var instanceKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(DataDirectory.ToUpperInvariant())))[..16];
        ShelfWindow.ConfigureActivation(instanceKey);
        using var mutex = new Mutex(true, "Local\\BOOTH-Shelf-" + instanceKey, out var firstInstance);
        if (!firstInstance)
        {
            if (!SmokeTest && !ShelfWindow.RestoreExistingWindow()) MessageBox.Show(NativeLanguage.Text("BOOTH Shelf 已在运行，请切换到已有窗口。"), "BOOTH Shelf");
            return;
        }
        Application.ThreadException += (_, eventArgs) => ReportFailure(eventArgs.Exception);
        try
        {
            _ = CoreWebView2Environment.GetAvailableBrowserVersionString();
            Application.Run(new ShelfWindow());
        }
        catch (Exception error)
        {
            ReportFailure(error);
        }
    }

    internal static void ReportFailure(Exception error)
    {
        File.WriteAllText(Path.Combine(DataDirectory, "startup-error.txt"), error.ToString());
        if (!SmokeTest)
            MessageBox.Show(NativeLanguage.Text("程序遇到错误：") + "\n" + NativeLanguage.Message(error.Message)
                + "\n\n" + NativeLanguage.Text("若缺少 Microsoft Edge WebView2 Runtime，请先安装该微软组件。") + "\n" + NativeLanguage.Text("错误详情保存在：")
                + Path.Combine(DataDirectory, "startup-error.txt"), "BOOTH Shelf", MessageBoxButtons.OK, MessageBoxIcon.Error);
        Environment.ExitCode = 1;
    }

    internal static string ReadAsset(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Assets." + name)
            ?? throw new FileNotFoundException(name);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}

internal sealed partial class ShelfWindow : Form
{
    private const string AppOrigin = "https://booth-shelf.local";
    internal const string LoginUrl = "https://booth.pm/users/sign_in";
    internal const string LibraryUrl = "https://accounts.booth.pm/library";
    private readonly WebView2 view = new() { Dock = DockStyle.Fill };
    private readonly string statePath = Path.Combine(Program.DataDirectory, "library.json");
    private readonly SemaphoreSlim storageLock = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private CoreWebView2Environment? environment;
    private BoothWindow? booth;
    private bool syncing;
    private CancellationTokenSource? syncCancellation;
    private bool smokeStarted;

    internal ShelfWindow()
    {
        Text = "BOOTH Shelf v0.5.10";
        Size = new Size(1320, 900);
        MinimumSize = new Size(880, 620);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(248, 249, 251);
        Controls.Add(view);
        InitializeFeatures();
        InitializeChrome();
        InitializeBackground();
        InitializeNotifications();
        if (Program.SmokeTest) { Opacity = 0; ShowInTaskbar = false; }
        Shown += async (_, _) => { _ = CheckCurrencyAsync(); await InitializeAsync(); };
        FormClosing += SaveSessionOnClosing;
        FormClosed += (_, _) => { DisposeNotifications(); DisposeBackground(); DisposeFeatures(); booth?.Dispose(); view.Dispose(); };
    }

    private async Task InitializeAsync()
    {
        try
        {
            var assetDirectory = Path.Combine(Program.DataDirectory, "ui-0.5.7");
            Directory.CreateDirectory(assetDirectory);
            foreach (var asset in new[] { "index.html", "styles.css", "app.js", "parser.js", "library-filters.js", "account-reader.js", "features.js", "filter-presets.js", "notifications.js", "i18n.js", "locales.json" })
                File.WriteAllText(Path.Combine(assetDirectory, asset), Program.ReadAsset(asset), new UTF8Encoding(false));
            environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Program.DataDirectory, "WebView2")).WaitAsync(TimeSpan.FromSeconds(30), lifetime.Token);
            await view.EnsureCoreWebView2Async(environment).WaitAsync(TimeSpan.FromSeconds(30), lifetime.Token);
            if (view.CoreWebView2 == null) throw new InvalidOperationException("WebView2 未能初始化，请关闭程序后重试。");
            await RestoreLoginSessionAsync();
            view.CoreWebView2.SetVirtualHostNameToFolderMapping("booth-shelf.local", assetDirectory, CoreWebView2HostResourceAccessKind.Deny);
            view.CoreWebView2.Settings.AreHostObjectsAllowed = false;
            view.CoreWebView2.Settings.IsStatusBarEnabled = false;
            view.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            view.CoreWebView2.PermissionRequested += (_, eventArgs) => eventArgs.State = CoreWebView2PermissionState.Deny;
            view.CoreWebView2.DownloadStarting += (_, eventArgs) => eventArgs.Cancel = true;
            view.CoreWebView2.WebMessageReceived += OnMessage;
            view.CoreWebView2.NavigationStarting += (_, eventArgs) =>
            {
                if (syncing && IsAppUri(eventArgs.Uri)) { eventArgs.Cancel = true; return; }
                if (IsAppUri(eventArgs.Uri)) { interfaceReady = false; return; }
                eventArgs.Cancel = true;
                OpenExternal(eventArgs.Uri);
            };
            view.CoreWebView2.NewWindowRequested += (_, eventArgs) =>
            {
                eventArgs.Handled = true;
                OpenExternal(eventArgs.Uri);
            };
            view.CoreWebView2.NavigationCompleted += async (_, eventArgs) =>
            {
                if (Program.SmokeTest && !smokeStarted && eventArgs.IsSuccess)
                {
                    smokeStarted = true;
                    await RunSmokeTestAsync();
                }
            };
            view.Source = new Uri(AppOrigin + "/index.html");
            StartSessionTimer();
        }
        catch (Exception error)
        {
            Program.ReportFailure(error);
            ExitApplication();
        }
    }

    private static bool IsAppUri(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.GetLeftPart(UriPartial.Authority) == AppOrigin;

    private static bool IsBoothUri(Uri uri) =>
        uri.Scheme == "https" && (uri.Host == "booth.pm" || uri.Host.EndsWith(".booth.pm", StringComparison.OrdinalIgnoreCase));

    private static void OpenExternal(string value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && IsBoothUri(uri))
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    private async void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs eventArgs)
    {
        if (!IsAppUri(eventArgs.Source)) return;
        string? requestId = null;
        try
        {
            var message = JsonNode.Parse(eventArgs.WebMessageAsJson)?.AsObject() ?? throw new InvalidDataException("无效请求");
            requestId = message["id"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(requestId) || requestId.Length > 100) return;
            var action = message["action"]?.GetValue<string>();
            object? result = action switch
            {
                "load" => await LoadStateAsync(),
                "login" => await OpenBoothAsync(),
                "auth-check" => await CheckAuthAsync(),
                "session-save" => await SaveLoginSessionNowAsync(),
                "session-remember" => await SetRememberSessionAsync(message["payload"]?["enabled"]?.GetValue<bool>() == true),
                "settings-save" => SaveSettings(message["payload"]),
                "tag-pin" => SetTagPinned(message["payload"]),
                "preset-save" => filterPresets.Save(message["payload"]),
                "preset-delete" => filterPresets.Delete(message["payload"]?["id"]?.GetValue<string>() ?? ""),
                "updates-list" => NotificationState(),
                "updates-check" => RequestUpdateCheck(),
                "updates-read" => ReadNotifications(message["payload"]?["id"]?.GetValue<string>()),
                "choose-directory" => ChooseDirectory(),
                "open-download-folder" => OpenDownloadFolder(),
                "window" => WindowAction(message["payload"]?["action"]?.GetValue<string>() ?? ""),
                "local-list" => localLibrary.List(),
                "local-action" => LocalAction(message["payload"]),
                "local-asset-folders" => LocalAssetFolders(message["payload"]),
                "local-asset-delete" => DeleteLocalAsset(message["payload"]),
                "currency-refresh" => await currencyScheduler.RefreshAsync(message["payload"]?["force"]?.GetValue<bool>() == true, lifetime.Token),
                "files" => await ListFilesAsync(message["payload"]?["id"]?.GetValue<string>() ?? ""),
                "downloads-start" => downloads.Enqueue(message["payload"]?["ticket"]?.GetValue<string>() ?? "",
                    message["payload"]?["ids"]?.AsArray().Select(id => id!.GetValue<string>()).ToArray() ?? []),
                "downloads-cancel" => downloads.Cancel(message["payload"]?["id"]?.GetValue<string>() ?? ""),
                "sync" => await SyncAsync(message["payload"]?["mode"]?.GetValue<string>() ?? "full"),
                "pause" => PauseSync(),
                "export" => await ExportAsync(),
                _ => throw new InvalidOperationException("未知操作")
            };
            Reply(new { id = requestId, result });
        }
        catch (OperationCanceledException)
        {
            Reply(new { id = requestId, error = "已暂停，成功读取的页面已保存。下次选择同一模式即可继续；原有完整资料库未覆盖。" });
        }
        catch (Exception error)
        {
            Reply(new { id = requestId, error = error.Message });
        }
    }

    private void Reply(object message)
    {
        if (!IsDisposed && !view.IsDisposed && view.CoreWebView2 != null)
            view.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message));
    }

    private async Task<JsonObject> LoadStateAsync()
    {
        await storageLock.WaitAsync(lifetime.Token);
        try
        {
            var state = ReadState();
            var cache = SyncCache.Load(Program.DataDirectory);
            if (state["snapshot"]?["schemaVersion"]?.GetValue<int>() != 2 && cache != null && !cache.Finished && cache.Pages.Count > 0)
            {
                state["pendingSnapshot"] = cache.Snapshot(state["snapshot"] as JsonObject, false, cache.Mode == "quick");
                state["pendingMode"] = cache.Mode;
            }
            foreach (var section in LibraryCoordinator.Sections)
            {
                var pending = SyncCache.Load(Path.Combine(Program.DataDirectory, "sections", section.Id));
                if (pending != null && !pending.Finished) { state["pendingMode"] = pending.Mode; break; }
            }
            metadata.Merge(state["snapshot"] as JsonObject);
            metadata.Merge(state["pendingSnapshot"] as JsonObject);
            state["settings"] = preferences.DeepClone();
            state["filterPresets"] = filterPresets.List();
            state["notifications"] = NotificationState();
            state["systemLanguage"] = NativeLanguage.SystemLanguage;
            state["downloads"] = downloads.History;
            state["localFiles"] = localLibrary.List();
            state["rates"] = currencyScheduler.Current.DeepClone();
            state["testing"] = Program.SmokeTest;
            state["session"] = sessionStatus.DeepClone();
            return state;
        }
        finally { storageLock.Release(); }
    }

    private JsonObject ReadState()
    {
        if (!File.Exists(statePath)) return new JsonObject();
        return JsonNode.Parse(File.ReadAllText(statePath))?.AsObject()
            ?? throw new InvalidDataException("本地资料库文件损坏，请先备份 library.json。");
    }

    private void WriteState(JsonObject state)
    {
        var temporary = statePath + ".tmp";
        File.WriteAllText(temporary, state.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        if (File.Exists(statePath)) File.Replace(temporary, statePath, statePath + ".bak");
        else File.Move(temporary, statePath);
    }

    private async Task<bool> SaveFavoritesAsync(JsonNode? payload)
    {
        if (payload is not JsonArray values || values.Count > 100000
            || values.Any(value => !Regex.IsMatch(value?.GetValue<string>() ?? "", @"^\d{1,20}$")))
            throw new InvalidDataException("收藏数据无效");
        await storageLock.WaitAsync(lifetime.Token);
        try
        {
            var state = ReadState();
            state["favorites"] = values.DeepClone();
            WriteState(state);
            return true;
        }
        finally { storageLock.Release(); }
    }

    private async Task<BoothWindow> EnsureBoothAsync()
    {
        if (environment == null) throw new InvalidOperationException("浏览器尚未准备好");
        if (booth == null || booth.IsDisposed)
        {
            booth = new BoothWindow();
            booth.Show(this);
            await booth.InitializeAsync(environment);
            booth.Browser.CoreWebView2.NavigationStarting += (_, args) => WatchAccountNavigation(args.Uri);
            booth.Browser.CoreWebView2.NavigationCompleted += async (_, args) =>
            {
                if (!syncing && !Program.SmokeTest && args.IsSuccess
                    && booth?.Browser.Source?.Host is "booth.pm" or "accounts.booth.pm")
                    await CheckAfterAccountNavigationAsync();
            };
        }
        return booth;
    }

    private async Task<bool> OpenBoothAsync()
    {
        if (syncing) throw new InvalidOperationException("请先暂停同步，再打开登录窗口。");
        var window = await EnsureBoothAsync();
        window.Show();
        window.Activate();
        if (!window.HasAccountFlow) await window.NavigateAsync(LibraryUrl, lifetime.Token);
        return true;
    }

    private bool PauseSync()
    {
        syncCancellation?.Cancel();
        return true;
    }

    private Task<JsonObject> SyncAsync(string mode) => SynchronizeAllAsync(mode);

    private async Task<bool> ExportAsync()
    {
        var state = await LoadStateAsync();
        using var dialog = new SaveFileDialog
        {
            Filter = NativeLanguage.Text("JSON 备份 (*.json)|*.json"),
            FileName = $"booth-shelf-{DateTime.Now:yyyy-MM-dd}.json",
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return false;
        state["schemaVersion"] = 1;
        File.WriteAllText(dialog.FileName, state.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        return true;
    }

    private async Task RunSmokeTestAsync()
    {
        try
        {
            await Task.Delay(800);
            var result = await view.ExecuteScriptAsync("""
                (() => {
                  const fixture = new DOMParser().parseFromString('<main><article><a href="https://artist.booth.pm/items/123"><img src="https://example.test/cover.jpg" alt="Test asset"></a><h2>Test asset</h2><a href="https://artist.booth.pm/">Test shop</a><a href="https://accounts.booth.pm/library/123">Library</a></article></main><a href="?page=2">2</a>', 'text/html');
                  const parsed = BoothParser.parse(fixture, 'https://accounts.booth.pm/library');
                  return {
                    title: document.title,
                    desktop: document.body.dataset.desktop,
                    ready: document.getElementById('sync').textContent.includes('同步'),
                    bridge: typeof chrome.webview.postMessage === 'function',
                    parser: parsed.items.length === 1 && parsed.items[0].id === '123' && parsed.items[0].shop === 'Test shop' && parsed.items[0].libraryUrl === 'https://accounts.booth.pm/library/123' && parsed.next === 'https://accounts.booth.pm/library?page=2',
                    libraryLink: safeURL('https://accounts.booth.pm/library/123', true) === 'https://accounts.booth.pm/library/123',
                    legacyLink: safeURL('https://booth.pm/ja/library', true) === 'https://accounts.booth.pm/library'
                  };
                })()
                """);
            var report = JsonNode.Parse(result)!;
            if (report["desktop"]?.GetValue<string>() != "true" || report["ready"]?.GetValue<bool>() != true
                || report["bridge"]?.GetValue<bool>() != true || report["parser"]?.GetValue<bool>() != true
                || report["libraryLink"]?.GetValue<bool>() != true || report["legacyLink"]?.GetValue<bool>() != true)
                throw new InvalidOperationException("桌面界面冒烟检查失败：" + result);
            SaveSettings(new JsonObject { ["currency"] = "CNY" });
            var state = await LoadStateAsync();
            if (state["settings"]?["currency"]?.GetValue<string>() != "CNY") throw new InvalidOperationException("本地存储检查失败");
            report["storage"] = true;
            report["syncEngine"] = await SyncSelfTest.RunAsync(Path.Combine(Program.DataDirectory, "sync-tests", Guid.NewGuid().ToString("N")));
            report["pageReader"] = await SyncSelfTest.ReadPagesAsync(environment!);
            report["metadata"] = await FeatureSelfTest.MetadataAsync(Path.Combine(Program.DataDirectory, "metadata-tests"));
            report["downloads"] = await FeatureSelfTest.DownloadsAsync(environment!, Path.Combine(Program.DataDirectory, "download-tests"));
            report["features"] = await FeatureSelfTest.InterfaceAsync(view);
            var uiResult = await view.ExecuteScriptAsync("""
                (() => {
                  snapshot = {
                    items: Array.from({length: 24000}, (_, index) => ({
                      id: String(index + 1), title: `TestAsset-${index + 1}`, shop: `Shop-${index % 8}`,
                      url: `https://artist.booth.pm/items/${index + 1}`, libraryUrl: 'https://accounts.booth.pm/library', image: '',
                      tags: ['VRChat', ['3D衣装','ヘアスタイル','アクセサリー','テクスチャ'][index % 4]],
                      category: '3Dモデル', subcategory: '3D衣装', forms: ['digital'], adult: false
                    })), pages: Array.from({length: 696}, () => ({})), syncedAt: new Date().toISOString(),
                    totalPages: 696, complete: true, syncKind: 'full'
                  };
                  renderShops();
                  render();
                  const bounded = document.querySelectorAll('.card').length === 80 && element('total').textContent === '24000';
                  element('next-page').click();
                  const nextPage = document.querySelector('.item-id').textContent === '#81';
                  element('search').value = 'TestAsset-23999';
                  element('search').dispatchEvent(new Event('input'));
                  const searched = document.querySelectorAll('.card').length === 1 && document.querySelector('.item-id').textContent === '#23999';
                  element('search').value = '';
                  element('search').dispatchEvent(new Event('input'));
                  const restored = displayPage === 1 && document.querySelectorAll('.card').length === 80;
                  element('sync-progress').hidden = false;
                  element('sync-meter').max = 696;
                  element('sync-meter').value = 500;
                  element('sync-detail').textContent = '500 / 696 页 · 测试数据';
                  notify('界面检查 · 24,000 件模拟商品，当前仅渲染 80 件。');
                  return {bounded, nextPage, searched, restored};
                })()
                """);
            var uiReport = JsonNode.Parse(uiResult)!.AsObject();
            if (uiReport.Any(value => value.Value?.GetValue<bool>() != true))
                throw new InvalidOperationException("大库界面检查失败：" + uiResult);
            report["largeLibraryUI"] = uiReport;
            await Task.Delay(150);
            using (var preview = File.Create(Path.Combine(Program.DataDirectory, "ui-preview.png")))
                await view.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, preview);
            File.WriteAllText(Path.Combine(Program.DataDirectory, "smoke-result.json"), report.ToJsonString());
        }
        catch (Exception error) { Program.ReportFailure(error); }
        finally { Close(); }
    }
}

internal sealed class BoothWindow : Form
{
    internal bool HasAccountFlow => ready && Browser.Source is { Scheme: "https" } source
        && (source.Host == "accounts.pixiv.net" || source.Host == "pixiv.net" || source.Host.EndsWith(".pixiv.net", StringComparison.OrdinalIgnoreCase)
            || ((source.Host == "booth.pm" || source.Host == "accounts.booth.pm")
                && Regex.IsMatch(source.AbsolutePath, @"^/(?:[a-z]{2}/)?(?:library(?:/|$)|users/sign_in(?:/|$)|signin(?:/|$)|login(?:/|$))")));
    internal WebView2 Browser { get; } = new() { Dock = DockStyle.Fill };
    private bool ready;
    private bool reading;
    private readonly ToolStripTextBox address = new() { AutoSize = false, Width = 480 };
    private readonly ToolStrip toolbar = new() { Dock = DockStyle.Top, GripStyle = ToolStripGripStyle.Hidden };
    private string hintSource = "";
    private readonly Label hint = new()
    {
        Dock = DockStyle.Top, Height = 52, Padding = new Padding(14, 8, 14, 4),
        BackColor = Color.FromArgb(255, 244, 235),
        Text = "正在打开 BOOTH 资料库并检查已有会话。\n只有网页要求时才需要登录；在资料库中能看到商品即可返回管理器同步。"
    };

    internal BoothWindow()
    {
        Size = new Size(1100, 820);
        StartPosition = FormStartPosition.CenterParent;
        toolbar.Items.Add("后退", null, (_, _) => { if (ready && Browser.CanGoBack) Browser.GoBack(); });
        toolbar.Items.Add("刷新", null, (_, _) => { if (ready) Browser.Reload(); });
        toolbar.Items.Add("首页", null, (_, _) => NavigateFromToolbar("https://booth.pm/"));
        toolbar.Items.Add("登录页", null, (_, _) => NavigateFromToolbar(ShelfWindow.LoginUrl));
        toolbar.Items.Add("Library", null, (_, _) => NavigateFromToolbar(ShelfWindow.LibraryUrl));
        toolbar.Items.Add(address);
        toolbar.Items.Add("前往", null, (_, _) => NavigateFromToolbar(address.Text));
        foreach (ToolStripItem item in toolbar.Items) if (item is not ToolStripTextBox) item.Tag = item.Text;
        hintSource = hint.Text;
        RefreshLanguage();
        address.KeyDown += (_, eventArgs) =>
        {
            if (eventArgs.KeyCode != Keys.Enter) return;
            eventArgs.SuppressKeyPress = true;
            NavigateFromToolbar(address.Text);
        };
        Controls.Add(Browser);
        Controls.Add(hint);
        Controls.Add(toolbar);
        FormClosing += (_, eventArgs) =>
        {
            if (eventArgs.CloseReason == CloseReason.UserClosing) { eventArgs.Cancel = true; Hide(); }
        };
    }

    internal void RefreshLanguage()
    {
        Text = NativeLanguage.Text("BOOTH · 账号 / Library");
        foreach (ToolStripItem item in toolbar.Items) if (item.Tag is string source) item.Text = NativeLanguage.Text(source);
        hint.Text = NativeLanguage.Message(hintSource);
    }

    private void SetHint(string source)
    {
        hintSource = source;
        hint.Text = NativeLanguage.Message(source);
    }

    internal async Task InitializeAsync(CoreWebView2Environment environment)
    {
        await Browser.EnsureCoreWebView2Async(environment).WaitAsync(TimeSpan.FromSeconds(30));
        if (Browser.CoreWebView2 == null) throw new InvalidOperationException("WebView2 未能初始化，请稍后重试。");
        Browser.CoreWebView2.Settings.AreHostObjectsAllowed = false;
        Browser.CoreWebView2.Settings.IsWebMessageEnabled = false;
        Browser.CoreWebView2.PermissionRequested += (_, eventArgs) => eventArgs.State = CoreWebView2PermissionState.Deny;
        Browser.CoreWebView2.DownloadStarting += (_, eventArgs) => eventArgs.Cancel = true;
        Browser.CoreWebView2.NewWindowRequested += (_, eventArgs) =>
        {
            eventArgs.Handled = true;
            if (reading) return;
            if (Uri.TryCreate(eventArgs.Uri, UriKind.Absolute, out var uri) && uri.Scheme == "https")
                Browser.CoreWebView2.Navigate(uri.AbsoluteUri);
        };
        Browser.CoreWebView2.NavigationStarting += (_, eventArgs) =>
        {
            if (reading) { eventArgs.Cancel = true; return; }
            if (!Uri.TryCreate(eventArgs.Uri, UriKind.Absolute, out var uri) || uri.Scheme != "https") eventArgs.Cancel = true;
        };
        Browser.CoreWebView2.SourceChanged += (_, _) => address.Text = Browser.Source?.AbsoluteUri ?? "";
        Browser.CoreWebView2.NavigationCompleted += (_, eventArgs) =>
        {
            if (eventArgs.HttpStatusCode >= 400)
                SetHint($"页面返回 HTTP {eventArgs.HttpStatusCode}，这不表示密码错误。\n请点击「登录页」或「首页」；仍失败时请提供当前地址（勿包含验证码或登录令牌）。");
            else if (!eventArgs.IsSuccess)
                SetHint($"页面加载失败：{eventArgs.WebErrorStatus}。\n请检查网络后刷新，或点击「登录页」重试。");
            else if (Browser.Source?.Host == "accounts.booth.pm" && Browser.Source.AbsolutePath.StartsWith("/library"))
                SetHint("BOOTH 资料库已打开，确认商品可见后可返回管理器同步。\n已有登录会话有效时，无需再次输入账号密码。");
            else
                SetHint("请按 BOOTH / pixiv 官方页面提示操作。\n可点击「Library」检查已有会话；程序不会自动填写或提交密码。");
        };
        ready = true;
    }

    private void NavigateFromToolbar(string value)
    {
        if (!ready || reading) return;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || !(uri.Host == "booth.pm" || uri.Host.EndsWith(".booth.pm", StringComparison.OrdinalIgnoreCase)
                || uri.Host == "pixiv.net" || uri.Host.EndsWith(".pixiv.net", StringComparison.OrdinalIgnoreCase)))
        {
            SetHint("请输入 BOOTH 或 pixiv 官方网站的 HTTPS 地址。");
            return;
        }
        Browser.CoreWebView2.Navigate(uri.AbsoluteUri);
    }

    internal async Task NavigateAsync(string url, CancellationToken cancellationToken)
    {
        if (!ready) throw new InvalidOperationException("登录窗口尚未准备好");
        var completion = new TaskCompletionSource<bool>();
        void Completed(object? sender, CoreWebView2NavigationCompletedEventArgs eventArgs)
        {
            if (eventArgs.HttpStatusCode >= 400)
                completion.TrySetException(new InvalidOperationException($"BOOTH 页面返回 HTTP {eventArgs.HttpStatusCode}。请在窗口点击「登录页」或「首页」重试，这不是密码错误。"));
            else if (eventArgs.IsSuccess) completion.TrySetResult(true);
            else completion.TrySetException(new InvalidOperationException("BOOTH 页面加载失败：" + eventArgs.WebErrorStatus));
        }
        Browser.CoreWebView2.NavigationCompleted += Completed;
        try
        {
            Browser.CoreWebView2.Navigate(url);
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(60), cancellationToken);
        }
        catch (TimeoutException) { throw new InvalidOperationException("BOOTH 页面加载超时，请检查网络后重试。"); }
        finally { Browser.CoreWebView2.NavigationCompleted -= Completed; }
    }

    internal void SetSyncing(bool value)
    {
        reading = value;
        Browser.Enabled = !value;
        foreach (var toolbar in Controls.OfType<ToolStrip>()) toolbar.Enabled = !value;
    }

    internal Task<string> PrepareReaderAsync() => Browser.ExecuteScriptAsync(Program.ReadAsset("page-reader.js"));

    internal async Task<LibraryPage> ReadPageAsync(int number, CancellationToken token)
    {
        var key = JsonSerializer.Serialize(Guid.NewGuid().ToString("N"));
        var consumed = false;
        try
        {
            token.ThrowIfCancellationRequested();
            var started = await Browser.ExecuteScriptAsync($"BoothPageReader.start({key}, {number});");
            if (started != "true") throw new InvalidOperationException("后台读取器未就绪，请重新同步。");
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(40))
            {
                await Task.Delay(150, token);
                var json = await Browser.ExecuteScriptAsync($"globalThis.BoothPageReader ? BoothPageReader.take({key}) : ({{error: 'BOOTH 页面已重新加载，请继续同步。'}});");
                var result = JsonNode.Parse(json);
                if (result == null) continue;
                consumed = true;
                if (result["error"] != null)
                    throw new SyncReadException($"第 {number} 页：{result["error"]!.GetValue<string>()}",
                        result["retryable"]?.GetValue<bool>() == true, result["retryAfterMs"]?.GetValue<int>() ?? 0);
                var page = result["data"]?.Deserialize<LibraryPage>(SyncCache.JsonOptions)
                    ?? throw new InvalidDataException("后台页面读取没有返回结果。");
                page.Validate(number);
                return page;
            }
            throw new SyncReadException($"第 {number} 页等待超时", true);
        }
        finally
        {
            if (!consumed && !Browser.IsDisposed)
            {
                try { await Browser.ExecuteScriptAsync($"globalThis.BoothPageReader?.cancel({key});"); }
                catch (Exception) { }
            }
        }
    }

    internal async Task<JsonObject> ParseAsync(CancellationToken cancellationToken)
    {
        JsonObject? result = null;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var json = await Browser.ExecuteScriptAsync(Program.ReadAsset("parser.js")
                + "\nJSON.stringify(BoothParser.parse(document, location.href));");
            var decoded = JsonSerializer.Deserialize<string>(json);
            result = decoded == null ? null : JsonNode.Parse(decoded)?.AsObject();
            if (result?["items"]?.AsArray().Count > 0 || result?["login"]?.GetValue<bool>() == true) break;
            await Task.Delay(700, cancellationToken);
        }
        return result ?? throw new InvalidOperationException("无法读取页面，请重新打开登录窗口。");
    }
}
