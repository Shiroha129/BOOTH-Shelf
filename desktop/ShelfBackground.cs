using Microsoft.Win32;

namespace BoothShelf;

internal sealed partial class ShelfWindow
{
    private readonly System.Windows.Forms.Timer currencyTimer = new();
    private CurrencyRefreshScheduler currencyScheduler = null!;
    private TrayController? tray;
    private bool exitRequested;
    private bool interfaceReady;

    private void InitializeBackground()
    {
        currencyScheduler = new CurrencyRefreshScheduler(currency);
        currencyScheduler.Refreshed += rates => Reply(new { type = "currency", rates });
        if (Program.SmokeTest) return;
        tray = new TrayController(this, ExitApplication);
        currencyTimer.Tick += async (_, _) => await CheckCurrencyAsync();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.TimeChanged += OnSystemTimeChanged;
    }

    private async Task CheckCurrencyAsync()
    {
        if (Program.SmokeTest || closingSession || IsDisposed) return;
        currencyTimer.Stop();
        try { await currencyScheduler.RefreshAsync(false, lifetime.Token); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally
        {
            if (!closingSession && !IsDisposed && !lifetime.IsCancellationRequested)
            {
                currencyTimer.Interval = currencyScheduler.NextDelayMilliseconds;
                currencyTimer.Start();
            }
        }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs args)
    {
        if (args.Mode == PowerModes.Resume) QueueCurrencyCheck();
    }

    private void OnSystemTimeChanged(object? sender, EventArgs args)
    {
        TimeZoneInfo.ClearCachedData();
        QueueCurrencyCheck();
    }

    private void QueueCurrencyCheck()
    {
        if (IsDisposed || Disposing || !IsHandleCreated) return;
        try { BeginInvoke(async () => await CheckCurrencyAsync()); }
        catch (InvalidOperationException) { }
    }

    private void DisposeBackground()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.TimeChanged -= OnSystemTimeChanged;
        currencyTimer.Dispose();
        tray?.Dispose();
    }

    private void ExitApplication()
    {
        exitRequested = true;
        Close();
    }

    private void ShowCloseChoice()
    {
        if (preferences["closeBehavior"]?.GetValue<string>() == "tray") { tray?.Hide(); return; }
        if (preferences["closeBehavior"]?.GetValue<string>() == "exit") { BeginInvoke(ExitApplication); return; }
        if (interfaceReady) { Reply(new { type = "close-request" }); return; }
        var hide = new TaskDialogButton(NativeLanguage.Text("缩小到托盘"));
        var exit = new TaskDialogButton(NativeLanguage.Text("直接关闭"));
        var cancel = new TaskDialogButton(NativeLanguage.Text("取消"));
        var remember = new TaskDialogVerificationCheckBox { Text = NativeLanguage.Text("不再提示") };
        var result = TaskDialog.ShowDialog(this, new TaskDialogPage
        {
            Caption = "BOOTH Shelf",
            Heading = NativeLanguage.Text("关闭 BOOTH Shelf"),
            Text = NativeLanguage.Text("缩小到托盘可继续同步、下载和更新汇率。直接关闭会停止这些任务。"),
            Buttons = { hide, exit, cancel },
            DefaultButton = hide,
            Verification = remember,
            AllowCancel = true
        });
        if (remember.Checked && (result == hide || result == exit))
            SaveSettings(new System.Text.Json.Nodes.JsonObject { ["closeBehavior"] = result == hide ? "tray" : "exit" });
        if (result == hide) tray?.Hide();
        else if (result == exit) BeginInvoke(ExitApplication);
    }
}
