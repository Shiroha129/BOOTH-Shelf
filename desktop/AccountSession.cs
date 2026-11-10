using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;

namespace BoothShelf;

internal sealed class AccountSession(BoothRequestPolicy? access = null) : IDisposable
{
    private readonly SessionBrowser browser = new(access);
    private readonly RequestGate requests = new(8);

    internal async Task InitializeAsync(CoreWebView2Environment environment, CancellationToken token, Action<SessionBrowser>? configure = null)
    {
        await browser.InitializeAsync(environment);
        browser.View.CoreWebView2.DownloadStarting += (_, args) => args.Cancel = true;
        configure?.Invoke(browser);
        await browser.NavigateAsync(ShelfWindow.LibraryUrl, token);
        var status = "null";
        for (var attempt = 0; attempt < 4; attempt++)
        {
            status = await browser.View.ExecuteScriptAsync(Program.ReadAsset("parser.js") + "\nBoothParser.authentication(document, location.href);");
            if (status is "true" or "false") break;
            if (attempt < 3) await Task.Delay(400, token);
        }
        if (status == "false") throw new InvalidOperationException("BOOTH 资料库跳转到登录页，请在账号窗口重新登录。");
        if (status != "true") throw new InvalidOperationException("暂时无法确认 BOOTH 会话，请在账号窗口打开 Library 检查网络或网页验证，然后重试。");
        await browser.View.ExecuteScriptAsync(Program.ReadAsset("account-reader.js"));
    }

    internal Task<JsonObject> ReadAsync(string target, string kind, CancellationToken token) => ReadAsync(target, kind, token, true);

    internal async Task<JsonObject> ReadAsync(string target, string kind, CancellationToken token, bool retry)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return await requests.RunAsync(() => access == null ? OnceAsync(target, kind, token) : access.RunAsync(() => OnceAsync(target, kind, token), token), token); }
            catch (SyncReadException error) when (error.Retryable && retry)
            {
                requests.Backoff(Math.Max(error.RetryAfterMs, 3000 * (1 << attempt)));
                if (attempt >= 3) throw;
            }
        }
    }

    private async Task<JsonObject> OnceAsync(string target, string kind, CancellationToken token)
    {
        var key = JsonSerializer.Serialize(Guid.NewGuid().ToString("N"));
        token.ThrowIfCancellationRequested();
        await browser.View.ExecuteScriptAsync($"AccountReader.start({key}, {JsonSerializer.Serialize(target)}, {JsonSerializer.Serialize(kind)});");
        try
        {
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(40))
            {
                await Task.Delay(100, token);
                var result = JsonNode.Parse(await browser.View.ExecuteScriptAsync($"AccountReader.take({key});"));
                if (result == null) continue;
                access?.Inspect(result["status"]?.GetValue<int>() ?? 0, result["challenge"]?.GetValue<bool>() == true,
                    TimeSpan.FromMilliseconds(result["retryAfterMs"]?.GetValue<int>() ?? 0));
                if (result["error"] != null)
                    throw new SyncReadException(result["error"]!.GetValue<string>(), result["retryable"]?.GetValue<bool>() == true, result["retryAfterMs"]?.GetValue<int>() ?? 0);
                return result["data"]!.AsObject();
            }
            throw new SyncReadException("BOOTH 请求超时", true);
        }
        finally
        {
            if (!browser.View.IsDisposed) await browser.View.ExecuteScriptAsync($"AccountReader.cancel({key});");
        }
    }

    public void Dispose() => browser.Dispose();
}
