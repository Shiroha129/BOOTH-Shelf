using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;

namespace BoothShelf;

internal sealed partial class ShelfWindow
{
    private readonly LoginSessionStore loginSession = new(Program.DataDirectory);
    private readonly SemaphoreSlim sessionLock = new(1, 1);
    private readonly System.Windows.Forms.Timer sessionTimer = new() { Interval = 5 * 60 * 1000 };
    private JsonObject sessionStatus = new() { ["state"] = "waiting", ["message"] = "登录成功后自动加密保存" };
    private bool sessionVerified;
    private int sessionRevision;
    private int accountCheckVersion;
    private bool closingSession;
    private bool sessionClosed;
    private bool RememberSession => preferences["rememberSession"]?.GetValue<bool>() != false;

    private static SessionCookie CopySessionCookie(CoreWebView2Cookie cookie) => new(cookie.Name, cookie.Value, cookie.Domain, cookie.Path,
        cookie.IsHttpOnly, cookie.IsSecure, (int)cookie.SameSite, cookie.IsSession ? null : new DateTimeOffset(cookie.Expires.ToUniversalTime()));

    private async Task<List<SessionCookie>> ReadSessionCookiesAsync(CancellationToken token)
    {
        var manager = view.CoreWebView2?.CookieManager ?? throw new InvalidOperationException("浏览器尚未准备好");
        var cookies = new Dictionary<string, SessionCookie>();
        foreach (var source in new[] { "https://booth.pm/", "https://accounts.booth.pm/library", "https://accounts.pixiv.net/", "https://www.pixiv.net/" })
        {
            var current = await manager.GetCookiesAsync(source).WaitAsync(TimeSpan.FromSeconds(5), token);
            foreach (var cookie in current)
            {
                var saved = CopySessionCookie(cookie);
                if (LoginSessionStore.AllowedDomain(saved.Domain)) cookies[saved.Key] = saved;
            }
        }
        return cookies.Values.ToList();
    }

    private void PublishSessionStatus(string state, string message, DateTimeOffset? savedAt = null)
    {
        sessionStatus = new JsonObject { ["state"] = state, ["message"] = message, ["savedAt"] = savedAt?.ToString("O") };
        Reply(new { type = "session", status = sessionStatus });
    }

    private async Task RestoreLoginSessionAsync()
    {
        if (Program.SmokeTest) return;
        if (!RememberSession) { PublishSessionStatus("disabled", "已关闭会话备份"); return; }
        await sessionLock.WaitAsync(lifetime.Token);
        try
        {
            var saved = loginSession.Load();
            if (saved == null) return;
            var cookies = await ReadSessionCookiesAsync(lifetime.Token);
            var missing = LoginSessionStore.MissingCookies(saved, cookies, DateTimeOffset.UtcNow);
            var manager = view.CoreWebView2.CookieManager;
            foreach (var stored in missing)
            {
                var cookie = manager.CreateCookie(stored.Name, stored.Value, stored.Domain, stored.Path);
                cookie.IsHttpOnly = stored.HttpOnly;
                cookie.IsSecure = stored.Secure;
                cookie.SameSite = (CoreWebView2CookieSameSiteKind)stored.SameSite;
                cookie.Expires = stored.Expires?.UtcDateTime ?? DateTime.MinValue;
                manager.AddOrUpdateCookie(cookie);
            }
            PublishSessionStatus("restored", missing.Count > 0 ? "已恢复加密会话，正在向 BOOTH 验证" : "已保留现有会话，正在向 BOOTH 验证", saved.SavedAt);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception) { PublishSessionStatus("error", "会话备份未能恢复，原文件已保留；可重新登录后保存"); }
        finally { sessionLock.Release(); }
    }

    private async Task SaveLoginSessionAsync(CancellationToken token)
    {
        if (Program.SmokeTest || !RememberSession || !sessionVerified) return;
        var revision = sessionRevision;
        await sessionLock.WaitAsync(token);
        try
        {
            if (!RememberSession || !sessionVerified || revision != sessionRevision) return;
            var cookies = await ReadSessionCookiesAsync(token);
            if (!RememberSession || !sessionVerified || revision != sessionRevision) return;
            var now = DateTimeOffset.UtcNow;
            if (loginSession.Save(cookies, true, now)) PublishSessionStatus("saved", "登录会话已加密保存", now);
            else PublishSessionStatus("waiting", "尚未读取到可保存的登录会话，旧备份保留");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (CryptographicException) { PublishSessionStatus("error", "Windows 加密暂不可用，未保存明文；请在正常用户桌面重新保存"); }
        catch (Exception) { PublishSessionStatus("error", "保存会话失败，旧备份保留；可稍后点击立即保存"); }
        finally { sessionLock.Release(); }
    }

    private void InvalidateLoginSession(string message)
    {
        sessionRevision++;
        sessionVerified = false;
        try
        {
            loginSession.Clear();
            PublishSessionStatus(RememberSession ? "waiting" : "disabled", message);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            preferences["rememberSession"] = false;
            try
            {
                SyncCache.AtomicWrite(preferencesPath, preferences.ToJsonString());
                PublishSessionStatus("error", "无法删除会话备份，已关闭自动恢复");
            }
            catch (Exception writeError) when (writeError is IOException or UnauthorizedAccessException)
            { PublishSessionStatus("error", "无法清除备份或保存设置，请检查数据目录权限后再次关闭保留登录"); }
        }
    }

    private async Task ObserveSessionAuthenticationAsync(bool? authenticated)
    {
        if (authenticated == true)
        {
            sessionVerified = true;
            await SaveLoginSessionAsync(lifetime.Token);
        }
        else if (authenticated == false)
            InvalidateLoginSession("当前会话需要重新登录，成功后自动保存新会话");
        else sessionVerified = false;
        Reply(new { type = "updates", state = NotificationState() });
    }

    private JsonObject SessionResponse() => new() { ["settings"] = preferences.DeepClone(), ["session"] = sessionStatus.DeepClone() };

    private async Task<JsonObject> SetRememberSessionAsync(bool enabled)
    {
        preferences["rememberSession"] = enabled;
        SyncCache.AtomicWrite(preferencesPath, preferences.ToJsonString());
        if (!enabled) InvalidateLoginSession("已关闭并删除会话备份，当前网页登录保留");
        else
        {
            PublishSessionStatus("waiting", "正在确认登录会话");
            await CheckAuthAsync();
        }
        return SessionResponse();
    }

    private async Task<JsonObject> SaveLoginSessionNowAsync()
    {
        if (!RememberSession) throw new InvalidOperationException("请先开启保留登录");
        await CheckAuthAsync();
        return SessionResponse();
    }

    private void WatchAccountNavigation(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !LoginSessionStore.AllowedDomain(uri.Host)) return;
        if (Regex.IsMatch(uri.AbsolutePath, @"/(?:sign_out|logout)(?:/|$)", RegexOptions.IgnoreCase))
            InvalidateLoginSession("已退出登录并清除会话备份");
    }

    private async Task CheckAfterAccountNavigationAsync()
    {
        var version = ++accountCheckVersion;
        try
        {
            await Task.Delay(600, lifetime.Token);
            if (version != accountCheckVersion || syncing || closingSession) return;
            if (authTask is { IsCompleted: false }) await authTask;
            if (version == accountCheckVersion && !syncing && !closingSession) await CheckAuthAsync();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    private void StartSessionTimer()
    {
        if (Program.SmokeTest) return;
        sessionTimer.Tick += async (_, _) =>
        {
            if (RememberSession && sessionVerified && !syncing && !closingSession)
                await CheckAuthAsync();
        };
        sessionTimer.Start();
    }

    private async void SaveSessionOnClosing(object? sender, FormClosingEventArgs args)
    {
        if (args.Cancel) return;
        if (sessionClosed || Program.SmokeTest || args.CloseReason is CloseReason.WindowsShutDown or CloseReason.TaskManagerClosing)
        { currencyTimer.Stop(); lifetime.Cancel(); return; }
        if (!exitRequested && !closingSession && args.CloseReason == CloseReason.UserClosing)
        {
            args.Cancel = true;
            ShowCloseChoice();
            return;
        }
        args.Cancel = true;
        if (closingSession) return;
        closingSession = true;
        updateTimer.Stop();
        updateCancellation?.Cancel();
        sessionTimer.Stop();
        currencyTimer.Stop();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        try { await SaveLoginSessionAsync(timeout.Token); }
        catch (OperationCanceledException) { }
        finally
        {
            sessionClosed = true;
            lifetime.Cancel();
            if (!IsDisposed && !Disposing && IsHandleCreated) BeginInvoke(Close);
        }
    }
}
