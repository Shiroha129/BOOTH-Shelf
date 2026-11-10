using System.Text.Json.Nodes;

namespace BoothShelf;

internal static class TraySelfTest
{
    internal static void Run(string root)
    {
        var report = new JsonObject();
        var language = NativeLanguage.Preference;
        using var window = new Form { Text = "BOOTH Shelf tray test", Opacity = 0, ShowInTaskbar = false };
        using var timeout = new System.Windows.Forms.Timer { Interval = 10000 };
        timeout.Tick += (_, _) => { report["passed"] = false; report["error"] = "Tray test timeout"; window.Close(); };
        window.Shown += async (_, _) =>
        {
            try
            {
                using var cancellation = new CancellationTokenSource();
                var exited = false;
                using var tray = new TrayController(window, () => { exited = true; window.Close(); });
                tray.Hide();
                Check(!window.Visible && !window.ShowInTaskbar && !window.IsDisposed && !cancellation.IsCancellationRequested, "缩小到托盘关闭了窗口或后台任务");
                await Task.Delay(80);
                Check(!window.Visible, "后台等待时窗口自行显示");
                tray.Menu.Items[0].PerformClick();
                Check(window.Visible && window.ShowInTaskbar && window.WindowState == FormWindowState.Normal, "托盘菜单未恢复窗口");
                window.WindowState = FormWindowState.Maximized;
                tray.Hide();
                tray.Restore();
                Check(window.WindowState == FormWindowState.Maximized, "最大化状态丢失");
                window.WindowState = FormWindowState.Normal;
                window.WindowState = FormWindowState.Minimized;
                tray.Hide();
                tray.Restore();
                Check(window.WindowState == FormWindowState.Normal && window.Visible, "最小化窗口无法从托盘恢复");
                foreach (var entry in new Dictionary<string, string>
                {
                    ["zh-CN"] = "退出程序", ["zh-TW"] = "結束程式", ["ja"] = "アプリを終了", ["en"] = "Quit", ["ko"] = "종료"
                })
                {
                    NativeLanguage.Preference = entry.Key;
                    tray.RefreshLanguage();
                    Check(tray.Menu.Items[2].Text == entry.Value, "托盘菜单语言错误：" + entry.Key);
                }
                tray.Hide();
                tray.Menu.Items[2].PerformClick();
                Check(exited && !window.Visible, "托盘退出菜单未关闭窗口");
                report["hideKeepsWindowAlive"] = true;
                report["restoreFromMenu"] = true;
                report["restoreWindowState"] = true;
                report["fiveLanguages"] = true;
                report["exitFromTray"] = true;
                report["passed"] = true;
            }
            catch (Exception error) { report["passed"] = false; report["error"] = error.ToString(); window.Close(); }
        };
        try { timeout.Start(); Application.Run(window); }
        catch (Exception error) { report["passed"] = false; report["error"] = error.ToString(); }
        finally
        {
            NativeLanguage.Preference = language;
            if (report["passed"]?.GetValue<bool>() != true) Environment.ExitCode = 1;
            File.WriteAllText(Path.Combine(root, "tray-result.json"), report.ToJsonString(new() { WriteIndented = true }));
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
