namespace BoothShelf;

internal sealed class TrayController : IDisposable
{
    private readonly Form window;
    private readonly NotifyIcon icon;
    internal ContextMenuStrip Menu { get; } = new();
    private FormWindowState restoreState = FormWindowState.Normal;
    internal event Action? NotificationClicked;

    internal TrayController(Form window, Action exit)
    {
        this.window = window;
        Menu.Items.Add("", null, (_, _) => Restore());
        Menu.Items.Add(new ToolStripSeparator());
        Menu.Items.Add("", null, (_, _) => exit());
        icon = new NotifyIcon { Icon = window.Icon, Text = "BOOTH Shelf", ContextMenuStrip = Menu, Visible = true };
        icon.DoubleClick += (_, _) => Restore();
        icon.BalloonTipClicked += (_, _) => { Restore(); NotificationClicked?.Invoke(); };
        window.Resize += RememberWindowState;
        RefreshLanguage();
    }

    private void RememberWindowState(object? sender, EventArgs args)
    {
        if (window.WindowState != FormWindowState.Minimized) restoreState = window.WindowState;
    }

    internal void Hide()
    {
        RememberWindowState(null, EventArgs.Empty);
        window.Hide();
        window.ShowInTaskbar = false;
    }

    internal void Restore()
    {
        if (window.IsDisposed) return;
        window.ShowInTaskbar = true;
        window.Show();
        window.WindowState = restoreState;
        window.Activate();
    }

    internal void RefreshLanguage()
    {
        Menu.Items[0].Text = NativeLanguage.Text("打开 BOOTH Shelf");
        Menu.Items[2].Text = NativeLanguage.Text("退出程序");
    }

    internal void ShowUpdates(int count) => icon.ShowBalloonTip(8000, "BOOTH Shelf",
        NativeLanguage.Message($"{count} 件资产的文件有更新"), ToolTipIcon.Info);

    public void Dispose()
    {
        window.Resize -= RememberWindowState;
        icon.Visible = false;
        icon.Dispose();
        Menu.Dispose();
    }
}
