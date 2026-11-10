using System.Diagnostics;

namespace BoothShelf;

internal sealed class RuntimeSetupDialog : Form
{
    private readonly Label status;
    private readonly ProgressBar progress;
    private readonly Button install;
    private readonly Button cancel;
    private readonly LinkLabel manual;
    private CancellationTokenSource? cancellation;
    private bool busy;
    private bool installing;

    internal RuntimeSetupDialog()
    {
        Text = "BOOTH Shelf";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(640, 320);
        using var iconStream = new MemoryStream(ReadIcon());
        using var icon = new Icon(iconStream);
        Icon = (Icon)icon.Clone();
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, RowCount = 6 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var height in new[] { 40, 72, 48, 24, 34, 54 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        layout.Controls.Add(new Label { Text = NativeLanguage.Text("首次运行准备"), Font = new Font(Font.FontFamily, 15, FontStyle.Bold), Dock = DockStyle.Fill }, 0, 0);
        layout.Controls.Add(new Label { Text = NativeLanguage.Text("此电脑缺少 Microsoft Edge WebView2 Runtime。点击下方按钮即可从微软下载并安装，完成后自动进入软件。需要联网，通常只需安装一次。"), Dock = DockStyle.Fill }, 0, 1);
        status = new Label { Dock = DockStyle.Fill, Text = NativeLanguage.Text("准备就绪"), AutoEllipsis = true };
        layout.Controls.Add(status, 0, 2);
        progress = new ProgressBar { Dock = DockStyle.Fill, Style = ProgressBarStyle.Marquee, Visible = false, Margin = new Padding(0, 3, 0, 9) };
        layout.Controls.Add(progress, 0, 3);
        manual = new LinkLabel { Text = NativeLanguage.Text("手动下载"), Dock = DockStyle.Fill };
        manual.LinkClicked += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(RuntimeInstaller.ManualUrl) { UseShellExecute = true }); }
            catch (Exception error) { status.Text = NativeLanguage.Message(error.Message); }
        };
        layout.Controls.Add(manual, 0, 4);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        install = new Button { Text = NativeLanguage.Text("一键安装并继续"), AutoSize = true, MinimumSize = new Size(170, 38) };
        cancel = new Button { Text = NativeLanguage.Text("取消"), AutoSize = true, MinimumSize = new Size(100, 38) };
        install.Click += async (_, _) => await InstallAsync();
        cancel.Click += (_, _) => { if (busy) cancellation?.Cancel(); else Close(); };
        actions.Controls.Add(install);
        actions.Controls.Add(cancel);
        layout.Controls.Add(actions, 0, 5);
        Controls.Add(layout);
        AcceptButton = install;
        CancelButton = cancel;
        FormClosing += (_, args) =>
        {
            if (!busy) return;
            args.Cancel = true;
            if (!installing) cancellation?.Cancel();
        };
    }

    private static byte[] ReadIcon()
    {
        using var stream = typeof(Program).Assembly.GetManifestResourceStream("Assets.app.ico")!;
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }

    private async Task InstallAsync()
    {
        busy = true;
        installing = false;
        install.Enabled = false;
        manual.Enabled = false;
        progress.Visible = true;
        status.ForeColor = SystemColors.ControlText;
        using var tokenSource = new CancellationTokenSource();
        cancellation = tokenSource;
        using var service = new RuntimeInstaller();
        var updates = new Progress<string>(message =>
        {
            installing = message == "正在安装 WebView2，请稍候…";
            cancel.Enabled = !installing;
            status.Text = NativeLanguage.Text(message);
        });
        try
        {
            await service.InstallAsync(Program.DataDirectory, updates, tokenSource.Token);
            busy = false;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (OperationCanceledException) { status.Text = NativeLanguage.Text("已取消，可稍后重试。"); }
        catch (Exception error) { status.Text = NativeLanguage.Message(error.Message); status.ForeColor = Color.Firebrick; }
        finally
        {
            busy = false;
            installing = false;
            cancellation = null;
            install.Enabled = true;
            cancel.Enabled = true;
            manual.Enabled = true;
            progress.Visible = false;
        }
    }
}
