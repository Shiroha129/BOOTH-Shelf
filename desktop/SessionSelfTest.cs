using System.Security.Cryptography;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace BoothShelf;

internal static class SessionSelfTest
{
    private sealed class FixtureProtection : ISessionProtection, IDisposable
    {
        private readonly byte[] key = RandomNumberGenerator.GetBytes(32);
        internal bool FailWrites;
        public byte[] Protect(byte[] plaintext)
        {
            if (FailWrites) throw new CryptographicException("Fixture encryption failure");
            var nonce = RandomNumberGenerator.GetBytes(12);
            var tag = new byte[16];
            var ciphertext = new byte[plaintext.Length];
            using var cipher = new AesGcm(key, tag.Length);
            cipher.Encrypt(nonce, plaintext, ciphertext, tag);
            return [.. nonce, .. tag, .. ciphertext];
        }
        public byte[] Unprotect(byte[] ciphertext)
        {
            if (ciphertext.Length < 28) throw new CryptographicException("Fixture invalid ciphertext");
            var plaintext = new byte[ciphertext.Length - 28];
            using var cipher = new AesGcm(key, 16);
            cipher.Decrypt(ciphertext.AsSpan(0, 12), ciphertext.AsSpan(28), ciphertext.AsSpan(12, 16), plaintext);
            return plaintext;
        }
        public void Dispose() => CryptographicOperations.ZeroMemory(key);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal static void Run(string root)
    {
        var report = new JsonObject();
        var run = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(run);
        try
        {
            report["store"] = StoreTests(run);
            report["windowsProtection"] = WindowsProtectionTest(run);
            report["passed"] = true;
        }
        catch (Exception error)
        {
            report["passed"] = false;
            report["error"] = error.ToString();
            Environment.ExitCode = 1;
        }
        File.WriteAllText(Path.Combine(root, "session-result.json"), report.ToJsonString(new() { WriteIndented = true }));
    }

    private static JsonObject StoreTests(string root)
    {
        using var protection = new FixtureProtection();
        var store = new LoginSessionStore(root, protection);
        var now = DateTimeOffset.UtcNow;
        var cookie = new SessionCookie("fixture_session", "FAKE_SECRET_FOR_ISOLATED_TEST_ONLY", ".booth.pm", "/", true, true, 1, null);
        var persistent = cookie with { Name = "fixture_persistent", Domain = "accounts.pixiv.net", Expires = now.AddHours(2), SameSite = 2 };
        var cookies = new[] { cookie, persistent, cookie with { Domain = "evilbooth.pm" }, cookie with { Domain = "booth.pm.evil.test" },
            cookie with { Domain = "other.example" }, cookie with { Name = "expired", Expires = now.AddMinutes(-1) } };
        Check(!store.Save(cookies, false, now) && !store.Exists, "未登录时写出了会话备份");
        Check(store.Save(cookies, true, now), "已验证会话未保存");
        var ciphertext = File.ReadAllBytes(Path.Combine(root, "login-session.dat"));
        Check(!Encoding.UTF8.GetString(ciphertext).Contains(cookie.Value) && !Encoding.UTF8.GetString(ciphertext).Contains(cookie.Name), "备份包含明文会话");
        var loaded = new LoginSessionStore(root, protection).Load()!;
        Check(loaded.Cookies.Count == 2 && loaded.SavedAt == now, "域过滤、过期过滤或重启读取失败");
        Check(loaded.Cookies[0].Expires == null && loaded.Cookies[0].HttpOnly && loaded.Cookies[0].Secure
            && loaded.Cookies[1].SameSite == 2 && loaded.Cookies[1].Expires == persistent.Expires, "Cookie 属性或有效期发生改变");
        var missing = LoginSessionStore.MissingCookies(loaded, [cookie with { Value = "newer_live_value" }], now);
        Check(missing.Count == 1 && missing[0].Name == persistent.Name, "覆盖了更新的现有会话");
        Check(LoginSessionStore.MissingCookies(loaded, [], now.AddHours(3)).Count == 1, "恢复了已过期 Cookie");
        Check(!store.Save([], true, now) && !store.Save([cookie with { Value = "logged_out" }], false, now), "空状态或未登录状态覆盖备份");
        Check(ciphertext.SequenceEqual(File.ReadAllBytes(Path.Combine(root, "login-session.dat"))), "失败保存改变了旧文件");
        protection.FailWrites = true;
        var refused = false;
        try { store.Save([cookie with { Value = "replacement" }], true, now); }
        catch (CryptographicException) { refused = true; }
        Check(refused && ciphertext.SequenceEqual(File.ReadAllBytes(Path.Combine(root, "login-session.dat"))), "加密失败仍替换备份");
        protection.FailWrites = false;
        store.Save([cookie with { Value = "second_verified_value" }], true, now.AddMinutes(1));
        File.WriteAllBytes(Path.Combine(root, "login-session.dat"), [1, 2, 3]);
        Check(store.Load()!.Cookies[0].Value == cookie.Value, "密文损坏后没有使用上一份备份");
        using var wrongKey = new FixtureProtection();
        var wrongUserRejected = false;
        try { new LoginSessionStore(root, wrongKey).Load(); }
        catch (InvalidDataException) { wrongUserRejected = true; }
        Check(wrongUserRejected && store.Exists, "错误密钥未拒绝或删除了原备份");
        store.Clear();
        Check(!store.Exists && !File.Exists(Path.Combine(root, "login-session.dat.tmp")), "退出后仍有可恢复备份");
        return new JsonObject
        {
            ["verifiedLoginOnly"] = true, ["encryptedFilesOnly"] = true, ["restartRestore"] = true, ["domainScope"] = true,
            ["preservesCookieFlags"] = true, ["doesNotExtendExpiry"] = true, ["keepsNewerCookies"] = true,
            ["failedSaveKeepsBackup"] = true, ["damagedPrimaryFallback"] = true, ["wrongKeyRejected"] = true, ["logoutClearsCopies"] = true
        };
    }

    private static JsonObject WindowsProtectionTest(string root)
    {
        var directory = Path.Combine(root, "dpapi");
        Directory.CreateDirectory(directory);
        var cookie = new SessionCookie("fixture", "ONLY_A_SYNTHETIC_COOKIE", "booth.pm", "/", true, true, 1, null);
        try
        {
            var store = new LoginSessionStore(directory);
            Check(store.Save([cookie], true, DateTimeOffset.UtcNow), "Windows 加密未保存");
            var restored = new LoginSessionStore(directory).Load();
            Check(restored?.Cookies.Single().Value == cookie.Value, "Windows 当前用户解密失败");
            Check(!Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(directory, "login-session.dat"))).Contains(cookie.Value), "Windows 加密文件含明文");
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            start.ArgumentList.Add("--session-verify");
            start.ArgumentList.Add("--data-dir");
            start.ArgumentList.Add(directory);
            using var verifier = Process.Start(start)!;
            Check(verifier.WaitForExit(15000) && verifier.ExitCode == 0, "新进程无法恢复 Windows 加密备份");
            store.Clear();
            return new JsonObject { ["currentUserRoundTrip"] = true, ["plaintextAbsent"] = true, ["newProcessRestore"] = true };
        }
        catch (CryptographicException error)
        {
            return new JsonObject { ["currentUserRoundTrip"] = false, ["blockedByEnvironment"] = true,
                ["errorType"] = error.GetType().Name, ["hresult"] = error.HResult };
        }
    }

    internal static void VerifyPersistedFixture(string root)
    {
        try
        {
            var saved = new LoginSessionStore(root).Load();
            Environment.ExitCode = saved?.Cookies.Count == 1 && saved.Cookies[0].Name == "fixture"
                && saved.Cookies[0].Value == "ONLY_A_SYNTHETIC_COOKIE" ? 0 : 1;
        }
        catch { Environment.ExitCode = 1; }
    }
}
