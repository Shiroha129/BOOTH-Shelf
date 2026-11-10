using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BoothShelf;

internal sealed record AppUpdatePlan(string DataDirectory, string Directory, string Target, string OriginalSha256,
    string Sha256, string Version, int ParentId, long ParentStarted, string ReadyEvent);

internal static class AppUpdateInstaller
{
    internal const string InstallError = "更新安装失败，旧版已保留，请重试。";
    internal const string WriteError = "程序目录不可写，请将软件移到可写目录后更新。";
    internal const string RestartError = "新版未能启动，已恢复旧版。";
    internal const string BackupError = "自动恢复未完成，旧版备份仍保留在程序目录。";
    private static string ResultPath(string root) => Path.Combine(root, "app-update-result.json");
    private static string Source(AppUpdatePlan plan) => Path.Combine(plan.Directory, "BOOTH-Shelf.exe");
    private static string Pending(AppUpdatePlan plan) => Path.Combine(Path.GetDirectoryName(plan.Target)!, ".booth-update-" + Path.GetFileName(plan.Directory) + ".tmp");
    internal static string Backup(AppUpdatePlan plan) => Path.Combine(Path.GetDirectoryName(plan.Target)!, ".booth-previous-" + Path.GetFileName(plan.Directory) + ".exe");

    internal static string Hash(string filename)
    {
        using var input = File.OpenRead(filename);
        return Convert.ToHexString(SHA256.HashData(input));
    }

    private static bool SamePath(string left, string right) => string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
        Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    internal static void RequireRegularPath(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || System.IO.Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException(InstallError);
    }

    internal static void Validate(AppUpdatePlan plan)
    {
        if (!SamePath(Path.GetDirectoryName(plan.Directory)!, Path.Combine(plan.DataDirectory, "app-updates"))
            || !Path.IsPathFullyQualified(plan.Target) || !plan.Target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || SamePath(plan.Target, Source(plan)) || !System.Version.TryParse(plan.Version, out _)
            || plan.ReadyEvent != "Local\\BOOTH-Shelf-Update-" + Path.GetFileName(plan.Directory))
            throw new InvalidDataException(InstallError);
        RequireRegularPath(plan.Directory);
        RequireRegularPath(plan.Target);
        RequireRegularPath(Source(plan));
        RequireRegularPath(Pending(plan));
        RequireRegularPath(Backup(plan));
        if (!string.Equals(Hash(plan.Target), plan.OriginalSha256, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Hash(Source(plan)), plan.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("更新文件校验失败，已停止安装。");
        AppUpdateClient.ValidateExecutable(Source(plan), System.Version.Parse(plan.Version));
    }

    internal static async Task LaunchAsync(PreparedAppUpdate update, string dataRoot, CancellationToken token)
    {
        var target = Environment.ProcessPath ?? throw new InvalidOperationException(InstallError);
        if (update.Version <= Program.AppVersion || !SamePath(update.Executable, Path.Combine(update.Directory, "BOOTH-Shelf.exe")))
            throw new InvalidDataException(InstallError);
        using var parent = Process.GetCurrentProcess();
        var plan = new AppUpdatePlan(Path.GetFullPath(dataRoot), Path.GetFullPath(update.Directory), target, Hash(target), update.Sha256,
            update.Version.ToString(3), parent.Id, parent.StartTime.ToUniversalTime().Ticks, "Local\\BOOTH-Shelf-Update-" + Path.GetFileName(update.Directory));
        Validate(plan);
        var probe = Pending(plan);
        try
        {
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            File.Delete(probe);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { throw new IOException(WriteError, error); }
        var helper = Path.Combine(plan.Directory, "apply-update.exe");
        var planFile = Path.Combine(plan.Directory, "update-plan.json");
        RequireRegularPath(helper);
        RequireRegularPath(planFile);
        File.Copy(target, helper, true);
        SyncCache.AtomicWrite(planFile, JsonSerializer.Serialize(plan));
        File.Delete(ResultPath(dataRoot));
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, plan.ReadyEvent);
        var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = plan.Directory };
        start.ArgumentList.Add("--apply-app-update");
        start.ArgumentList.Add(planFile);
        using var process = Process.Start(start) ?? throw new InvalidOperationException(InstallError);
        try
        {
            var started = Stopwatch.StartNew();
            while (!ready.WaitOne(0))
            {
                token.ThrowIfCancellationRequested();
                if (process.HasExited || started.Elapsed > TimeSpan.FromSeconds(30)) throw new IOException(InstallError);
                await Task.Delay(100, token);
            }
        }
        catch
        {
            if (!process.HasExited) process.Kill();
            throw;
        }
    }

    internal static async Task<int> RunHelperAsync(string planFile)
    {
        AppUpdatePlan? plan = null;
        var trusted = false;
        var parentExited = false;
        try
        {
            var helper = Environment.ProcessPath ?? throw new InvalidDataException(InstallError);
            if (!SamePath(planFile, Path.Combine(Path.GetDirectoryName(helper)!, "update-plan.json")) || new FileInfo(planFile).Length > 16384)
                throw new InvalidDataException(InstallError);
            plan = JsonSerializer.Deserialize<AppUpdatePlan>(File.ReadAllText(planFile)) ?? throw new InvalidDataException(InstallError);
            Validate(plan);
            if (!SamePath(helper, Path.Combine(plan.Directory, "apply-update.exe"))
                || !string.Equals(Hash(helper), plan.OriginalSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException(InstallError);
            using var parent = Process.GetProcessById(plan.ParentId);
            if (parent.StartTime.ToUniversalTime().Ticks != plan.ParentStarted || !SamePath(parent.MainModule!.FileName, plan.Target))
                throw new InvalidDataException(InstallError);
            trusted = true;
            NativeLanguage.Initialize(plan.DataDirectory);
            using (var ready = EventWaitHandle.OpenExisting(plan.ReadyEvent)) ready.Set();
            await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90));
            parentExited = true;
            await ApplyAsync(plan, StartUpdatedAsync);
            return 0;
        }
        catch (Exception error)
        {
            if (trusted && plan != null)
            {
                try { WriteResult(plan, false, error.Message == BackupError ? BackupError : InstallError); }
                catch (Exception) { }
                if (parentExited && error.Message != BackupError)
                {
                    try { using var restored = StartProcess(plan, null); }
                    catch (Exception) { MessageBox.Show(NativeLanguage.Text(InstallError), "BOOTH Shelf", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                }
            }
            return 1;
        }
    }

    internal static async Task ApplyAsync(AppUpdatePlan plan, Func<AppUpdatePlan, bool, Task> restart)
    {
        Validate(plan);
        var pending = Pending(plan);
        var backup = Backup(plan);
        var replaced = false;
        try
        {
            File.Copy(Source(plan), pending, false);
            if (!string.Equals(Hash(pending), plan.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException(InstallError);
            var started = Stopwatch.StartNew();
            while (true)
            {
                try { File.Replace(pending, plan.Target, backup); replaced = true; break; }
                catch (IOException) when (started.Elapsed < TimeSpan.FromSeconds(20)) { await Task.Delay(250); }
            }
            try { await restart(plan, true); }
            catch
            {
                File.Replace(backup, plan.Target, null);
                replaced = false;
                WriteResult(plan, false, RestartError);
                await restart(plan, false);
                return;
            }
            WriteResult(plan, true, "");
        }
        catch
        {
            if (replaced && File.Exists(backup))
            {
                try { File.Replace(backup, plan.Target, null); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { WriteResult(plan, false, BackupError); throw new IOException(BackupError, error); }
            }
            throw;
        }
        finally
        {
            try { if (File.Exists(pending)) File.Delete(pending); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static Process StartProcess(AppUpdatePlan plan, string? readyEvent)
    {
        var start = new ProcessStartInfo(plan.Target) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(plan.Target)! };
        start.ArgumentList.Add("--data-dir");
        start.ArgumentList.Add(plan.DataDirectory);
        if (readyEvent != null)
        {
            start.ArgumentList.Add("--app-update-ready");
            start.ArgumentList.Add(readyEvent);
        }
        return Process.Start(start) ?? throw new IOException(RestartError);
    }

    private static async Task StartUpdatedAsync(AppUpdatePlan plan, bool verifyStartup)
    {
        if (!verifyStartup) { using var restored = StartProcess(plan, null); return; }
        var name = "Local\\BOOTH-Shelf-Started-" + Path.GetFileName(plan.Directory);
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, name);
        using var process = StartProcess(plan, name);
        try { await WaitForStartupAsync(process, ready, TimeSpan.FromSeconds(90)); }
        catch
        {
            if (!process.HasExited)
            {
                process.Kill();
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
            throw;
        }
    }

    internal static async Task WaitForStartupAsync(Process process, EventWaitHandle ready, TimeSpan timeout)
    {
        var started = Stopwatch.StartNew();
        while (!ready.WaitOne(0))
        {
            if (process.HasExited || started.Elapsed >= timeout) throw new IOException(RestartError);
            await Task.Delay(100);
        }
    }

    internal static void SignalStartupReady()
    {
        if (Program.AppUpdateReadyEvent == null) return;
        try { using var ready = EventWaitHandle.OpenExisting(Program.AppUpdateReadyEvent); ready.Set(); }
        catch (WaitHandleCannotBeOpenedException) { }
    }

    private static void WriteResult(AppUpdatePlan plan, bool success, string error) => SyncCache.AtomicWrite(ResultPath(plan.DataDirectory),
        new JsonObject { ["success"] = success, ["version"] = plan.Version, ["error"] = error, ["backup"] = Backup(plan), ["directory"] = plan.Directory }.ToJsonString());

    internal static JsonObject? ReadResult(string root)
    {
        try
        {
            var path = ResultPath(root);
            return File.Exists(path) && new FileInfo(path).Length <= 16384 ? JsonNode.Parse(File.ReadAllText(path))?.AsObject() : null;
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    internal static async Task CompleteStartupAsync(string root)
    {
        try
        {
            var result = ReadResult(root);
            for (var attempt = 0; result == null && Program.AppUpdateReadyEvent != null && attempt < 20; attempt++)
            {
                await Task.Delay(250);
                result = ReadResult(root);
            }
            if (result == null) return;
            if (result["success"]?.GetValue<bool>() == false) { File.Delete(ResultPath(root)); return; }
            var directory = result["directory"]?.GetValue<string>();
            if (string.IsNullOrEmpty(directory) || !SamePath(Path.GetDirectoryName(directory)!, Path.Combine(root, "app-updates"))) return;
            RequireRegularPath(directory);
            var planFile = Path.Combine(directory, "update-plan.json");
            if (!File.Exists(planFile) || new FileInfo(planFile).Length > 16384) return;
            var plan = JsonSerializer.Deserialize<AppUpdatePlan>(File.ReadAllText(planFile));
            if (plan == null || !SamePath(plan.Directory, directory) || !SamePath(plan.DataDirectory, root)
                || !SamePath(plan.Target, Environment.ProcessPath!) || plan.Version != Program.AppVersion.ToString(3)
                || !string.Equals(Hash(plan.Target), plan.Sha256, StringComparison.OrdinalIgnoreCase)) return;
            var backup = Backup(plan);
            RequireRegularPath(backup);
            if (File.Exists(backup) && string.Equals(Hash(backup), plan.OriginalSha256, StringComparison.OrdinalIgnoreCase)) File.Delete(backup);
            for (var attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    foreach (var name in new[] { "apply-update.exe", "BOOTH-Shelf.exe", "update-plan.json" })
                    {
                        var file = Path.Combine(directory, name);
                        RequireRegularPath(file);
                        File.Delete(file);
                    }
                    System.IO.Directory.Delete(directory, false);
                    File.Delete(ResultPath(root));
                    return;
                }
                catch (IOException) { await Task.Delay(500); }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { }
    }
}
