using Microsoft.Win32;
using System.Text.Json.Nodes;

namespace BoothShelf;

internal static class WindowsStartupSelfTest
{
    internal static JsonObject Run(string root)
    {
        var registryPath = @"Software\BOOTH-Shelf-StartupTest-" + Guid.NewGuid().ToString("N");
        var executable = Path.Combine(root, "测试 程序", "BOOTH-Shelf.exe");
        var dataDirectory = Path.Combine(root, "用户 数据");
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(registryPath, true);
            key.SetValue("Unrelated", "keep");
            Check(!WindowsStartup.IsEnabled(key, executable, dataDirectory), "新用户的开机自启不是关闭状态");
            WindowsStartup.Write(key, true, executable, dataDirectory);
            var expected = $"\"{executable}\" --data-dir \"{dataDirectory}\"";
            Check((string?)key.GetValue(WindowsStartup.ValueName) == expected, "启动命令未保留含空格和中文的程序或数据目录");
            Check(key.GetValueKind(WindowsStartup.ValueName) == RegistryValueKind.String, "启动项类型错误");
            using (var reopened = Registry.CurrentUser.OpenSubKey(registryPath))
                Check(WindowsStartup.IsEnabled(reopened, executable, dataDirectory), "开机自启设置未持久保存");
            Check(!WindowsStartup.IsEnabled(key, executable, dataDirectory + "-other"), "启动项误匹配到其他数据目录");
            WindowsStartup.Write(key, false, executable, dataDirectory);
            WindowsStartup.Write(key, false, executable, dataDirectory);
            Check(key.GetValue(WindowsStartup.ValueName) == null && (string?)key.GetValue("Unrelated") == "keep", "关闭开机自启未只删除本程序的启动项");
            return new JsonObject { ["defaultDisabled"] = true, ["quotedPaths"] = true, ["persistedRegistration"] = true, ["removesOnlyOwnValue"] = true };
        }
        finally { Registry.CurrentUser.DeleteSubKey(registryPath, false); }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
