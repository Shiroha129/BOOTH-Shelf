using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;

namespace BoothShelf;

internal sealed partial class ShelfWindow
{
    private readonly string preferencesPath = Path.Combine(Program.DataDirectory, "settings.json");
    private JsonObject preferences = new();
    private MetadataCatalog metadata = null!;
    private DownloadService downloads = null!;
    private LocalLibrary localLibrary = null!;
    private CurrencyService currency = null!;
    private FilterPresetStore filterPresets = null!;
    private PurchaseCatalog purchases = null!;
    private BoothRequestPolicy boothAccess = null!;
    private SessionBrowser? authentication;
    private Task<object>? authTask;

    private void InitializeFeatures()
    {
        if (File.Exists(preferencesPath))
            preferences = JsonNode.Parse(File.ReadAllText(preferencesPath))?.AsObject() ?? new JsonObject();
        preferences["downloadDirectory"] ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "BOOTH-Shelf");
        preferences.Remove("autoMetadata");
        preferences["currency"] ??= "CNY";
        preferences.Remove("autoUpdates");
        preferences["rememberSession"] ??= true;
        preferences["pinnedTags"] ??= new JsonArray();
        preferences["language"] ??= "auto";
        preferences["closeBehavior"] ??= "ask";
        preferences["startWithWindows"] = !Program.SmokeTest && WindowsStartup.Enabled;
        preferences["updateNotifications"] ??= true;
        preferences["notificationIntervalMinutes"] ??= 60;
        boothAccess = new BoothRequestPolicy(Program.DataDirectory);
        boothAccess.Changed += BoothAccessChanged;
        filterPresets = new FilterPresetStore(Program.DataDirectory);
        metadata = new MetadataCatalog(Program.DataDirectory, access: boothAccess);
        currency = new CurrencyService(Program.DataDirectory);
        purchases = new PurchaseCatalog(Program.DataDirectory);
        downloads = new DownloadService(Program.DataDirectory,
            () => Task.FromResult(environment ?? throw new InvalidOperationException("浏览器尚未准备好")),
            () => preferences["downloadDirectory"]!.GetValue<string>(), lifetime.Token, access: boothAccess);
        downloads.Changed += Reply;
        localLibrary = new LocalLibrary(Program.DataDirectory, downloads.History);
        downloads.Completed += record =>
        {
            localLibrary.Add(record);
            RefreshNotificationLibrary();
            Reply(new { type = "local-files", items = localLibrary.List() });
        };
    }

    private void DisposeFeatures()
    {
        downloads.Changed -= Reply;
        boothAccess.Changed -= BoothAccessChanged;
        metadata.Dispose();
        currency.Dispose();
        downloads.Dispose();
        authentication?.Dispose();
        sessionTimer.Dispose();
    }

    private JsonObject SaveSettings(JsonNode? payload)
    {
        var updated = (JsonObject)preferences.DeepClone();
        if (payload?["language"] is JsonValue language)
        {
            var code = language.GetValue<string>();
            if (code is not ("auto" or "zh-CN" or "zh-TW" or "ja" or "en" or "ko")) throw new InvalidDataException("不支持的语言");
            updated["language"] = code;
        }
        if (payload?["currency"] is JsonValue chosen)
        {
            var value = chosen.GetValue<string>();
            if (!CurrencyService.Currencies.Contains(value)) throw new InvalidDataException("不支持的货币");
            updated["currency"] = value;
        }
        if (payload?["closeBehavior"] is JsonValue behavior)
        {
            var value = behavior.GetValue<string>();
            if (value is not ("ask" or "tray" or "exit")) throw new InvalidDataException("关闭方式无效");
            updated["closeBehavior"] = value;
        }
        if (payload?["updateNotifications"] is JsonValue notifications) updated["updateNotifications"] = notifications.GetValue<bool>();
        if (payload?["startWithWindows"] is JsonValue startup) updated["startWithWindows"] = startup.GetValue<bool>();
        if (payload?["notificationIntervalMinutes"] is JsonValue interval)
        {
            var minutes = interval.GetValue<int>();
            if (!UpdateMonitor.Intervals.Contains(minutes)) throw new InvalidDataException("通知检查间隔无效");
            updated["notificationIntervalMinutes"] = minutes;
        }
        SyncCache.AtomicWrite(preferencesPath, updated.ToJsonString());
        if (!Program.SmokeTest && payload?["startWithWindows"] != null) WindowsStartup.SetEnabled(updated["startWithWindows"]!.GetValue<bool>());
        preferences = updated;
        updateMonitor.SetInterval(preferences["notificationIntervalMinutes"]!.GetValue<int>());
        updateMonitor.SystemNotificationsEnabled = preferences["updateNotifications"]!.GetValue<bool>();
        if (!updateMonitor.Enabled) updateCancellation?.Cancel();
        NativeLanguage.Preference = preferences["language"]?.GetValue<string>() ?? "auto";
        booth?.RefreshLanguage();
        tray?.RefreshLanguage();
        return (JsonObject)preferences.DeepClone();
    }

    private JsonArray SetTagPinned(JsonNode? payload)
    {
        var tag = payload?["tag"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(tag) || tag.Length > 300 || payload?["pinned"] is not JsonValue value || !value.TryGetValue<bool>(out var pinned))
            throw new InvalidDataException("标签收藏请求无效");
        var tags = (preferences["pinnedTags"] as JsonArray ?? new JsonArray()).Select(item => item!.GetValue<string>()).Distinct().ToList();
        if (pinned && !tags.Contains(tag)) tags.Add(tag);
        if (!pinned) tags.Remove(tag);
        if (tags.Count > 2000) throw new InvalidOperationException("收藏标签已达到上限");
        var updated = (JsonObject)preferences.DeepClone();
        updated["pinnedTags"] = new JsonArray(tags.Select(item => (JsonNode)JsonValue.Create(item)!).ToArray());
        SyncCache.AtomicWrite(preferencesPath, updated.ToJsonString());
        preferences = updated;
        return (JsonArray)preferences["pinnedTags"]!.DeepClone();
    }

    private JsonObject ChooseDirectory()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = NativeLanguage.Text("选择 BOOTH 文件下载目录"),
            UseDescriptionForTitle = true,
            SelectedPath = preferences["downloadDirectory"]!.GetValue<string>()
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            preferences["downloadDirectory"] = Path.GetFullPath(dialog.SelectedPath);
            SyncCache.AtomicWrite(preferencesPath, preferences.ToJsonString());
        }
        return (JsonObject)preferences.DeepClone();
    }

    private bool OpenDownloadFolder(string? id = null)
    {
        var path = id == null ? preferences["downloadDirectory"]!.GetValue<string>() : downloads.GetFolder(id);
        if (id == null) Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        return true;
    }

    private bool CopyProductUrl(string id)
    {
        if (!Regex.IsMatch(id, @"^\d{1,20}$")) throw new InvalidDataException("商品 ID 无效");
        Clipboard.SetText("https://booth.pm/ja/items/" + id);
        return true;
    }

    private async Task<bool> SaveProductImageAsync(JsonNode? payload)
    {
        var id = payload?["id"]?.GetValue<string>() ?? "";
        var number = payload?["number"]?.GetValue<int>() ?? 0;
        if (!Regex.IsMatch(id, @"^\d{1,20}$") || number is < 1 or > 10000) throw new InvalidDataException("图片链接无效。");
        var image = await metadata.ImageAsync(payload?["url"]?.GetValue<string>() ?? "", lifetime.Token);
        using var dialog = new SaveFileDialog
        {
            Title = NativeLanguage.Text("保存图片"),
            Filter = NativeLanguage.Text("图片") + " (*" + image.Extension + ")|*" + image.Extension,
            FileName = $"BOOTH-{id}-{number}{image.Extension}",
            DefaultExt = image.Extension.TrimStart('.'),
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return false;
        var temporary = dialog.FileName + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, image.Data, lifetime.Token);
            File.Move(temporary, dialog.FileName, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return true;
    }

    private async Task<bool> CopyProductImageAsync(JsonNode? payload)
    {
        var image = await metadata.ImageAsync(payload?["url"]?.GetValue<string>() ?? "", lifetime.Token);
        await CopyImageToClipboardAsync(view.CoreWebView2, image.Data, image.Extension);
        return true;
    }

    internal static async Task CopyImageToClipboardAsync(CoreWebView2 browser, byte[] bytes, string extension)
    {
        try
        {
            // WebView2 decodes the web image formats that GDI+ cannot read.
            if (extension is ".webp" or ".avif")
            {
                var source = JsonSerializer.Serialize("data:image/" + extension[1..] + ";base64," + Convert.ToBase64String(bytes));
                var result = JsonNode.Parse(await browser.CallDevToolsProtocolMethodAsync("Runtime.evaluate", JsonSerializer.Serialize(new
                {
                    expression = $$"""
                        (async () => {
                          const image = new Image();
                          image.src = {{source}};
                          await image.decode();
                          if (!image.naturalWidth || image.naturalWidth * image.naturalHeight > 64 * 1024 * 1024) throw new Error('Image size');
                          const canvas = document.createElement('canvas');
                          canvas.width = image.naturalWidth;
                          canvas.height = image.naturalHeight;
                          canvas.getContext('2d').drawImage(image, 0, 0);
                          return canvas.toDataURL('image/png').split(',')[1];
                        })()
                        """,
                    awaitPromise = true,
                    returnByValue = true
                })).WaitAsync(TimeSpan.FromSeconds(30)));
                var encoded = result?["result"]?["value"]?.GetValue<string>();
                if (string.IsNullOrEmpty(encoded) || encoded.Length > 70 * 1024 * 1024) throw new InvalidDataException();
                bytes = Convert.FromBase64String(encoded);
            }
            using var input = new MemoryStream(bytes);
            using var bitmap = new Bitmap(input);
            if ((long)bitmap.Width * bitmap.Height > 64 * 1024 * 1024) throw new InvalidDataException();
            using var png = new MemoryStream();
            bitmap.Save(png, System.Drawing.Imaging.ImageFormat.Png);
            png.Position = 0;
            var clipboard = new DataObject();
            clipboard.SetImage(bitmap);
            clipboard.SetData("PNG", false, png);
            Clipboard.SetDataObject(clipboard, true, 5, 100);
        }
        catch (Exception error)
        {
            throw new InvalidOperationException("图片复制失败，请稍后重试。", error);
        }
    }

    private JsonArray LocalAction(JsonNode? payload)
    {
        localLibrary.Act(payload?["id"]?.GetValue<string>() ?? "", payload?["action"]?.GetValue<string>() ?? "");
        RefreshNotificationLibrary();
        return localLibrary.List();
    }

    private string[] LocalAssetFolders(JsonNode? payload)
    {
        var itemId = payload?["itemId"]?.GetValue<string>() ?? "";
        if (downloads.HasActiveItem(itemId)) throw new InvalidOperationException("该资产正在下载，请等待完成或取消下载后再删除。");
        return localLibrary.AssetDirectories(itemId);
    }

    private JsonArray DeleteLocalAsset(JsonNode? payload)
    {
        LocalAssetFolders(payload);
        localLibrary.DeleteAsset(payload?["itemId"]?.GetValue<string>() ?? "",
            payload?["directories"]?.AsArray().Select(directory => directory!.GetValue<string>()).ToArray() ?? []);
        RefreshNotificationLibrary();
        return localLibrary.List();
    }

    private async Task<JsonObject> ListFilesAsync(string id)
    {
        if (!Regex.IsMatch(id, @"^\d{1,20}$")) throw new InvalidDataException("商品 ID 无效");
        var state = await LoadStateAsync();
        var items = (state["pendingSnapshot"] ?? state["snapshot"])?["items"] as JsonArray;
        var item = items?.OfType<JsonObject>().FirstOrDefault(item => item["id"]?.GetValue<string>() == id)
            ?? throw new InvalidOperationException("商品不在本地资料库中，请先同步。");
        var sections = item["sections"] as JsonArray ?? new JsonArray(item["section"]?.GetValue<string>() ?? "bought");
        if (!sections.Any(section => section?.GetValue<string>() is "bought" or "gift"))
            throw new InvalidOperationException("免费栏仅展示商品，请在商品页面下载。");
        return await downloads.ListAsync(item);
    }

    private Task<object> CheckAuthAsync()
    {
        if (authTask is { IsCompleted: false }) return authTask;
        return authTask = ProbeAuthenticationAsync();
    }

    private async Task<object> ProbeAuthenticationAsync()
    {
        var loginRevision = sessionRevision;
        bool? authenticated = null;
        try
        {
            if (environment == null) return new { authenticated };
            if (authentication == null)
            {
                var candidate = new SessionBrowser(boothAccess);
                try
                {
                    await candidate.InitializeAsync(environment);
                    candidate.View.CoreWebView2.DownloadStarting += (_, args) => args.Cancel = true;
                    authentication = candidate;
                }
                catch { candidate.Dispose(); throw; }
            }
            await authentication.NavigateAsync(LibraryUrl, lifetime.Token);
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var result = await authentication.View.ExecuteScriptAsync(Program.ReadAsset("parser.js")
                    + "\nBoothParser.authentication(document, location.href);");
                if (result == "true") authenticated = true;
                else if (result == "false") authenticated = false;
                if (authenticated != null) break;
                if (attempt < 3) await Task.Delay(400, lifetime.Token);
            }
            WriteAuthenticationDiagnostic(authenticated, authentication.View.Source, null);
        }
        catch (Exception error)
        {
            var source = authentication?.View.Source;
            authentication?.Dispose();
            authentication = null;
            WriteAuthenticationDiagnostic(null, source, error.GetType().Name);
        }
        if (loginRevision == sessionRevision) await ObserveSessionAuthenticationAsync(authenticated);
        else authenticated = null;
        var message = new { type = "auth", authenticated };
        Reply(message);
        return message;
    }

    private static void WriteAuthenticationDiagnostic(bool? authenticated, Uri? source, string? errorType)
    {
        try
        {
            var record = new JsonObject
            {
                ["checkedAt"] = DateTimeOffset.UtcNow.ToString("O"),
                ["state"] = authenticated == true ? "authenticated" : authenticated == false ? "login-required" : "unknown",
                ["host"] = source?.Host,
                ["page"] = source?.AbsolutePath.StartsWith("/library") == true ? "library"
                    : source != null && Regex.IsMatch(source.AbsolutePath, @"/(?:sign_in|signin|login)(?:/|$)") ? "login" : "other",
                ["errorType"] = errorType
            };
            SyncCache.AtomicWrite(Path.Combine(Program.DataDirectory, "auth-status.json"), record.ToJsonString());
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
