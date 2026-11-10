using System.Text.Json.Nodes;

namespace BoothShelf;

internal sealed partial class ShelfWindow
{
    private UpdateMonitor updateMonitor = null!;
    private readonly System.Windows.Forms.Timer updateTimer = new() { Interval = 1500 };
    private CancellationTokenSource? updateCancellation;
    private Task? updateTask;
    private AccountSession? updateSession;
    private bool NotificationsEnabled => preferences["updateNotifications"]?.GetValue<bool>() != false;
    private string? notificationError;

    private void InitializeNotifications()
    {
        updateMonitor = new UpdateMonitor(Program.DataDirectory, access: boothAccess);
        updateMonitor.SetInterval(preferences["notificationIntervalMinutes"]!.GetValue<int>());
        RefreshNotificationLibrary();
        updateMonitor.NewNotifications += count => { if (!closingSession && NotificationsEnabled) tray?.ShowUpdates(count); };
        updateMonitor.Progress += () => { if (!closingSession && !IsDisposed) Reply(new { type = "updates", state = NotificationState() }); };
        if (Program.SmokeTest) return;
        if (tray != null) tray.NotificationClicked += () => Reply(new { type = "updates-open" });
        updateTimer.Tick += async (_, _) => await CheckUpdatesAsync();
        updateTimer.Start();
    }

    private JsonObject NotificationState()
    {
        var state = updateMonitor.State();
        state["enabled"] = NotificationsEnabled;
        state["status"] = !NotificationsEnabled ? "disabled" : boothAccess.Paused ? "paused" : !sessionVerified ? "login" : syncing ? "syncing"
            : notificationError != null || state["issue"]?.GetValue<bool>() == true ? "error" : "active";
        return state;
    }

    private async Task CheckUpdatesAsync()
    {
        if (Program.SmokeTest || !NotificationsEnabled || !sessionVerified || syncing || closingSession || IsDisposed || boothAccess.Paused || !updateMonitor.Due || updateTask is { IsCompleted: false }) return;
        updateTimer.Stop();
        updateTask = RunUpdateBatchAsync();
        try { await updateTask; }
        finally { if (!closingSession && !IsDisposed && !lifetime.IsCancellationRequested) updateTimer.Start(); }
    }

    private async Task RunUpdateBatchAsync()
    {
        RefreshNotificationLibrary();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        updateCancellation = cancellation;
        var publish = false;
        try
        {
            publish = await updateMonitor.CheckBatchAsync(async (url, token) =>
            {
                if (updateSession == null)
                {
                    var candidate = new AccountSession(boothAccess);
                    try { await candidate.InitializeAsync(environment!, token); updateSession = candidate; }
                    catch { candidate.Dispose(); throw; }
                }
                try
                {
                    var page = await updateSession.ReadAsync(url, "library", token, retry: false);
                    if (page["login"]?.GetValue<bool>() == true) throw new InvalidOperationException("请先登录 BOOTH 后检查更新");
                    return page;
                }
                catch { updateSession?.Dispose(); updateSession = null; throw; }
            }, cancellation.Token);
            notificationError = null;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { publish = true; }
        catch (Exception) { notificationError = "error"; publish = true; }
        finally
        {
            updateCancellation = null;
            if (publish && !IsDisposed && !closingSession) Reply(new { type = "updates", state = NotificationState() });
        }
    }

    private async Task PauseUpdateChecksAsync()
    {
        updateCancellation?.Cancel();
        if (updateTask != null) await updateTask;
        updateSession?.Dispose();
        updateSession = null;
    }

    private JsonObject RequestUpdateCheck()
    {
        boothAccess.ThrowIfPaused();
        RefreshNotificationLibrary();
        if (!sessionVerified) throw new InvalidOperationException("请先登录 BOOTH 后检查更新");
        if (!NotificationsEnabled) throw new InvalidOperationException("请先在设置中开启资产更新通知");
        updateMonitor.RequestCheck();
        _ = CheckUpdatesAsync();
        return NotificationState();
    }

    private JsonObject ReadNotifications(string? id)
    {
        updateMonitor.MarkRead(id);
        return NotificationState();
    }

    private void RefreshNotificationLibrary(JsonObject? snapshot = null)
    {
        if (updateMonitor == null) return;
        updateMonitor.SetDownloadedLibrary(snapshot ?? ReadState()["snapshot"] as JsonObject, localLibrary.List());
        Reply(new { type = "updates", state = NotificationState() });
    }

    private void BoothAccessChanged()
    {
        if (IsDisposed || closingSession) return;
        if (InvokeRequired) { BeginInvoke(BoothAccessChanged); return; }
        Reply(new { type = "booth-access", state = boothAccess.State() });
        if (updateMonitor != null) Reply(new { type = "updates", state = NotificationState() });
    }

    private JsonObject ResumeBoothAccess()
    {
        var state = boothAccess.Resume();
        downloads.ResumePending();
        _ = CheckAuthAsync();
        return state;
    }

    private void DisposeNotifications()
    {
        updateTimer.Dispose();
        updateCancellation?.Cancel();
        updateMonitor.Dispose();
        updateSession?.Dispose();
    }
}
