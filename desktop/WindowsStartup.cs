using Microsoft.Win32;

namespace BoothShelf;

internal static class WindowsStartup
{
    private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string ValueName = "BOOTH-Shelf";

    internal static bool Enabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunPath);
            return IsEnabled(key, Environment.ProcessPath!, Program.DataDirectory);
        }
    }

    internal static void SetEnabled(bool enabled)
    {
        using var key = enabled ? Registry.CurrentUser.CreateSubKey(RunPath, true) : Registry.CurrentUser.OpenSubKey(RunPath, true);
        if (key != null) Write(key, enabled, Environment.ProcessPath!, Program.DataDirectory);
    }

    internal static string Command(string executable, string dataDirectory) =>
        $"\"{Path.GetFullPath(executable)}\" --data-dir \"{Path.GetFullPath(dataDirectory)}\"";

    internal static bool IsEnabled(RegistryKey? key, string executable, string dataDirectory) =>
        key?.GetValue(ValueName) is string value && string.Equals(value, Command(executable, dataDirectory), StringComparison.OrdinalIgnoreCase);

    internal static void Write(RegistryKey key, bool enabled, string executable, string dataDirectory)
    {
        if (enabled) key.SetValue(ValueName, Command(executable, dataDirectory), RegistryValueKind.String);
        else key.DeleteValue(ValueName, false);
    }
}
