using System.Runtime.InteropServices;

namespace BoothShelf;

internal sealed partial class ShelfWindow
{
    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr word, IntPtr parameter);
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr handle, uint message, IntPtr word, IntPtr parameter);
    private static uint restoreWindowMessage;

    internal static void ConfigureActivation(string key) => restoreWindowMessage = RegisterWindowMessage("BOOTH-Shelf-Restore-" + key);
    internal static bool RestoreExistingWindow() => restoreWindowMessage != 0 && PostMessage((IntPtr)0xffff, restoreWindowMessage, IntPtr.Zero, IntPtr.Zero);

    protected override void WndProc(ref Message message)
    {
        if (restoreWindowMessage != 0 && message.Msg == restoreWindowMessage)
        {
            if (!closingSession) tray?.Restore();
            return;
        }
        base.WndProc(ref message);
    }

    private void InitializeChrome()
    {
        FormBorderStyle = FormBorderStyle.None;
        MaximizedBounds = Screen.FromHandle(Handle).WorkingArea;
        using var stream = typeof(Program).Assembly.GetManifestResourceStream("Assets.app.ico");
        if (stream != null) Icon = new Icon(stream);
        var rounded = 2;
        DwmSetWindowAttribute(Handle, 33, ref rounded, sizeof(int));
    }

    private bool WindowAction(string action)
    {
        switch (action)
        {
            case "close": BeginInvoke(Close); break;
            case "ready": interfaceReady = true; Reply(new { type = "currency", rates = currencyScheduler.Current }); break;
            case "tray": if (!closingSession) tray?.Hide(); break;
            case "exit": BeginInvoke(ExitApplication); break;
            case "minimize": WindowState = FormWindowState.Minimized; break;
            case "maximize":
                MaximizedBounds = Screen.FromHandle(Handle).WorkingArea;
                WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
                break;
            case "drag":
                ReleaseCapture();
                SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero);
                break;
            default:
                var edges = new Dictionary<string, int> { ["left"] = 10, ["right"] = 11, ["top"] = 12, ["top-left"] = 13,
                    ["top-right"] = 14, ["bottom"] = 15, ["bottom-left"] = 16, ["bottom-right"] = 17 };
                if (WindowState == FormWindowState.Normal && edges.TryGetValue(action, out var edge))
                {
                    ReleaseCapture();
                    SendMessage(Handle, 0xA1, (IntPtr)edge, IntPtr.Zero);
                }
                break;
        }
        return true;
    }
}
