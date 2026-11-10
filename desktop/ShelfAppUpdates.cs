using System.Diagnostics;
using System.Text.Json.Nodes;

namespace BoothShelf;

internal sealed partial class ShelfWindow
{
    private readonly AppUpdateClient appUpdateClient = new();
    private readonly System.Windows.Forms.Timer appUpdateTimer = new() { Interval = 6 * 60 * 60 * 1000 };
    private CancellationTokenSource? appUpdateCancellation;
    private AppRelease? appRelease;
    private PreparedAppUpdate? preparedAppUpdate;
    private string appUpdateStatus = "idle";
    private string appUpdateError = "";
    private DateTimeOffset? appUpdateChecked;
    private long appUpdateReceived;
    private long appUpdateTotal;
    private bool appUpdateStarted;
    private bool AppUpdateBusy => appUpdateStatus is "checking" or "downloading" or "installing";

    private void InitializeAppUpdates()
    {
        var previous = AppUpdateInstaller.ReadResult(Program.DataDirectory);
        if (previous?["success"]?.GetValue<bool>() == false)
        {
            appUpdateStatus = "error";
            appUpdateError = previous["error"]?.GetValue<string>() ?? AppUpdateInstaller.InstallError;
        }
        appUpdateTimer.Tick += (_, _) => RequestAppUpdateCheck(true);
    }

    private JsonObject AppUpdateState() => new()
    {
        ["status"] = appUpdateStatus, ["currentVersion"] = Program.AppVersion.ToString(3),
        ["version"] = appRelease?.Version.ToString(3), ["releaseUrl"] = appRelease?.ReleaseUrl,
        ["received"] = appUpdateReceived, ["total"] = appUpdateTotal, ["error"] = appUpdateError,
        ["lastCheckedAt"] = appUpdateChecked?.ToString("O")
    };

    private void PublishAppUpdate() => Reply(new { type = "app-update", state = AppUpdateState() });

    private void StartAppUpdates()
    {
        if (appUpdateStarted || Program.SmokeTest) return;
        appUpdateStarted = true;
        AppUpdateInstaller.SignalStartupReady();
        _ = Task.Run(() => AppUpdateInstaller.CompleteStartupAsync(Program.DataDirectory));
        appUpdateTimer.Start();
        RequestAppUpdateCheck(true);
    }

    private JsonObject RequestAppUpdateCheck(bool automatic = false)
    {
        if (AppUpdateBusy || preparedAppUpdate != null || closingSession) return AppUpdateState();
        if (!automatic) appUpdateError = "";
        appUpdateStatus = "checking";
        PublishAppUpdate();
        _ = CheckAppUpdateAsync();
        return AppUpdateState();
    }

    private async Task CheckAppUpdateAsync()
    {
        try
        {
            appRelease = await appUpdateClient.CheckAsync(Program.AppVersion, lifetime.Token);
            appUpdateChecked = DateTimeOffset.Now;
            appUpdateStatus = appRelease == null ? "current" : "available";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
        catch (Exception)
        {
            appUpdateStatus = "error";
            appUpdateError = "无法检查软件更新，请检查网络后重试。";
        }
        if (!closingSession) PublishAppUpdate();
    }

    private JsonObject StartAppUpdateDownload()
    {
        if (AppUpdateBusy || closingSession) return AppUpdateState();
        if (preparedAppUpdate != null) return RequestAppUpdateApply();
        if (appRelease == null) throw new InvalidOperationException("请先检查软件更新。");
        if (syncing || downloads.Busy) throw new InvalidOperationException("请先暂停同步并等待资产下载完成，再更新软件。");
        appUpdateCancellation?.Dispose();
        appUpdateCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        appUpdateStatus = "downloading";
        appUpdateError = "";
        appUpdateReceived = 0;
        appUpdateTotal = appRelease.Size;
        PublishAppUpdate();
        _ = DownloadAppUpdateAsync(appRelease, appUpdateCancellation.Token);
        return AppUpdateState();
    }

    private async Task DownloadAppUpdateAsync(AppRelease release, CancellationToken token)
    {
        var published = Stopwatch.StartNew();
        var progress = new Progress<AppDownloadProgress>(value =>
        {
            if (appUpdateStatus != "downloading" || token.IsCancellationRequested) return;
            appUpdateReceived = value.Received;
            appUpdateTotal = value.Total;
            if (published.ElapsedMilliseconds < 200 && value.Received != value.Total) return;
            published.Restart();
            PublishAppUpdate();
        });
        try
        {
            preparedAppUpdate = await appUpdateClient.DownloadAsync(release, Path.Combine(Program.DataDirectory, "app-updates"), progress, token);
            appUpdateStatus = "ready";
            if (!token.IsCancellationRequested && !closingSession) await ApplyAppUpdateAsync();
        }
        catch (OperationCanceledException)
        {
            appUpdateStatus = "available";
            appUpdateError = "";
        }
        catch (Exception)
        {
            appUpdateStatus = "error";
            appUpdateError = "更新下载或校验失败，旧版未更改，请重试。";
        }
        if (!closingSession) PublishAppUpdate();
    }

    private JsonObject CancelAppUpdateDownload()
    {
        if (appUpdateStatus == "downloading") appUpdateCancellation?.Cancel();
        return AppUpdateState();
    }

    private JsonObject RequestAppUpdateApply()
    {
        if (!AppUpdateBusy && preparedAppUpdate != null && !closingSession) _ = ApplyAppUpdateAsync();
        return AppUpdateState();
    }

    private async Task ApplyAppUpdateAsync()
    {
        if (preparedAppUpdate == null || closingSession) return;
        if (syncing || downloads.Busy)
        {
            appUpdateStatus = "ready";
            appUpdateError = "请先暂停同步并等待资产下载完成，再更新软件。";
            PublishAppUpdate();
            return;
        }
        appUpdateStatus = "installing";
        appUpdateError = "";
        PublishAppUpdate();
        try
        {
            await AppUpdateInstaller.LaunchAsync(preparedAppUpdate, Program.DataDirectory, lifetime.Token);
            ExitApplication();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            appUpdateStatus = "ready";
            appUpdateError = error.Message == AppUpdateInstaller.WriteError ? AppUpdateInstaller.WriteError : AppUpdateInstaller.InstallError;
            PublishAppUpdate();
        }
    }

    private bool OpenAppUpdateRelease()
    {
        var url = appRelease?.ReleaseUrl ?? "https://github.com/Shiroha129/BOOTH-Shelf/releases";
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        return true;
    }

    private void DisposeAppUpdates()
    {
        appUpdateTimer.Dispose();
        appUpdateCancellation?.Cancel();
        appUpdateCancellation?.Dispose();
        appUpdateClient.Dispose();
    }
}
