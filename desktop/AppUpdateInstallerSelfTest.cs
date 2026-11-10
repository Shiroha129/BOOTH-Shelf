using System.Text.Json.Nodes;

namespace BoothShelf;

internal static class AppUpdateInstallerSelfTest
{
    internal static async Task<JsonObject> RunAsync(string root)
    {
        var original = Environment.ProcessPath!;
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(data);
        var library = Path.Combine(data, "library.json");
        var session = Path.Combine(data, "session.fixture");
        File.WriteAllText(library, "synthetic library");
        File.WriteAllText(session, "synthetic encrypted session placeholder");
        AppUpdatePlan Plan(string name)
        {
            var directory = Path.Combine(data, "app-updates", name);
            Directory.CreateDirectory(directory);
            var source = Path.Combine(directory, "BOOTH-Shelf.exe");
            var target = Path.Combine(root, name + ".exe");
            File.Copy(original, source);
            File.WriteAllText(target, "synthetic old executable");
            return new AppUpdatePlan(data, directory, target, AppUpdateInstaller.Hash(target), AppUpdateInstaller.Hash(source),
                Program.AppVersion.ToString(3), Environment.ProcessId, 0, "Local\\BOOTH-Shelf-Update-" + name);
        }
        var success = Plan("success");
        var restarted = 0;
        await AppUpdateInstaller.ApplyAsync(success, (plan, verify) =>
        {
            Check(AppUpdateInstaller.Hash(plan.Target) == plan.Sha256, "Restart ran before new executable was installed");
            restarted++;
            return Task.CompletedTask;
        });
        Check(restarted == 1 && File.ReadAllText(AppUpdateInstaller.Backup(success)) == "synthetic old executable", "Previous executable backup missing");
        Check(AppUpdateInstaller.ReadResult(data)?["success"]?.GetValue<bool>() == true, "Successful install result missing");
        var rollback = Plan("rollback");
        var attempts = 0;
        await AppUpdateInstaller.ApplyAsync(rollback, (plan, verify) =>
        {
            if (++attempts == 1) throw new IOException("synthetic launch failure");
            Check(File.ReadAllText(plan.Target) == "synthetic old executable", "Failed launch did not restore original");
            return Task.CompletedTask;
        });
        Check(attempts == 2 && AppUpdateInstaller.ReadResult(data)?["success"]?.GetValue<bool>() == false, "Rollback was not reported");
        var corrupt = Plan("corrupt");
        File.AppendAllText(Path.Combine(corrupt.Directory, "BOOTH-Shelf.exe"), "changed");
        await Reject(() => AppUpdateInstaller.ApplyAsync(corrupt, (_, _) => throw new Exception("Corrupt file executed")));
        Check(File.ReadAllText(corrupt.Target) == "synthetic old executable", "Corrupt update replaced old executable");
        var changed = Plan("target-changed");
        File.WriteAllText(changed.Target, "another version was installed");
        await Reject(() => AppUpdateInstaller.ApplyAsync(changed, (_, _) => throw new Exception("Changed target executed")));
        Check(File.ReadAllText(changed.Target) == "another version was installed", "Unexpected target was overwritten");
        await Reject(() => AppUpdateInstaller.ApplyAsync(success with { Directory = root }, (_, _) => Task.CompletedTask));
        using var running = System.Diagnostics.Process.GetCurrentProcess();
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset);
        var readiness = AppUpdateInstaller.WaitForStartupAsync(running, ready, TimeSpan.FromSeconds(1));
        Check(!readiness.IsCompleted, "Startup was confirmed before the ready signal");
        ready.Set();
        await readiness;
        ready.Reset();
        var timeoutRejected = false;
        try { await AppUpdateInstaller.WaitForStartupAsync(running, ready, TimeSpan.FromMilliseconds(10)); }
        catch (IOException) { timeoutRejected = true; }
        Check(timeoutRejected, "Unresponsive startup was accepted");
        Check(File.ReadAllText(library) == "synthetic library" && File.ReadAllText(session) == "synthetic encrypted session placeholder", "Account data changed");
        return new JsonObject { ["replacementAndRestart"] = true, ["backup"] = true, ["rollbackOnLaunchFailure"] = true,
            ["tamperedUpdateRejected"] = true, ["changedTargetPreserved"] = true, ["stagingBoundary"] = true, ["userDataPreserved"] = true,
            ["startupReadinessRequired"] = true, ["startupTimeout"] = true };
    }

    private static async Task Reject(Func<Task> action)
    {
        try { await action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Invalid update plan was accepted");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
