using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BoothShelf;

internal sealed record SessionCookie(string Name, string Value, string Domain, string Path,
    bool HttpOnly, bool Secure, int SameSite, DateTimeOffset? Expires)
{
    internal string Key => Domain.ToLowerInvariant() + "\n" + Path + "\n" + Name;
}

internal sealed record SavedLoginSession(int Version, DateTimeOffset SavedAt, List<SessionCookie> Cookies);

internal interface ISessionProtection
{
    byte[] Protect(byte[] plaintext);
    byte[] Unprotect(byte[] ciphertext);
}

internal sealed class WindowsSessionProtection : ISessionProtection
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("BOOTH-Shelf/login-session/v1");
    public byte[] Protect(byte[] plaintext) => ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
    public byte[] Unprotect(byte[] ciphertext) => ProtectedData.Unprotect(ciphertext, Entropy, DataProtectionScope.CurrentUser);
}

internal sealed class LoginSessionStore(string root, ISessionProtection? protection = null)
{
    private const int MaximumBytes = 2 * 1024 * 1024;
    private readonly string path = System.IO.Path.Combine(root, "login-session.dat");
    private readonly ISessionProtection protector = protection ?? new WindowsSessionProtection();
    internal bool Exists => File.Exists(path) || File.Exists(path + ".bak");

    internal static bool AllowedDomain(string domain) => domain.TrimStart('.').ToLowerInvariant() is
        "booth.pm" or "accounts.booth.pm" or "pixiv.net" or "accounts.pixiv.net" or "www.pixiv.net";

    internal static bool IsUsable(SessionCookie cookie, DateTimeOffset now) => AllowedDomain(cookie.Domain)
        && !cookie.Domain.StartsWith("..") && cookie.Name.Length is > 0 and <= 512 && cookie.Value.Length <= 32768
        && cookie.Path.StartsWith('/') && cookie.Path.Length <= 2048
        && !cookie.Name.Any(char.IsControl) && !cookie.Value.Any(char.IsControl) && !cookie.Path.Any(char.IsControl)
        && cookie.SameSite is >= 0 and <= 2 && (cookie.Expires == null || cookie.Expires > now);

    internal bool Save(IEnumerable<SessionCookie> cookies, bool authenticated, DateTimeOffset now)
    {
        if (!authenticated) return false;
        var selected = cookies.Where(cookie => IsUsable(cookie, now)).DistinctBy(cookie => cookie.Key).ToList();
        if (selected.Count == 0) return false;
        if (selected.Count > 512) throw new InvalidDataException("登录会话数据过多，未替换备份。");
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(new SavedLoginSession(1, now, selected));
        byte[]? ciphertext = null;
        try
        {
            if (plaintext.Length > MaximumBytes) throw new InvalidDataException("登录会话数据过大，未替换备份。");
            ciphertext = protector.Protect(plaintext);
            using (var output = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
            {
                output.Write(ciphertext);
                output.Flush(true);
            }
            if (File.Exists(path)) File.Replace(path + ".tmp", path, path + ".bak", true);
            else File.Move(path + ".tmp", path);
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            if (ciphertext != null) CryptographicOperations.ZeroMemory(ciphertext);
        }
    }

    internal SavedLoginSession? Load()
    {
        var failed = false;
        foreach (var candidate in new[] { path, path + ".bak" })
        {
            if (!File.Exists(candidate)) continue;
            byte[]? plaintext = null;
            try
            {
                if (new FileInfo(candidate).Length > MaximumBytes) throw new InvalidDataException();
                plaintext = protector.Unprotect(File.ReadAllBytes(candidate));
                var session = JsonSerializer.Deserialize<SavedLoginSession>(plaintext);
                if (session == null || session.Version != 1 || session.Cookies == null || session.Cookies.Count > 512
                    || session.Cookies.Any(cookie => cookie == null || cookie.Name == null || cookie.Value == null || cookie.Domain == null || cookie.Path == null))
                    throw new InvalidDataException();
                return session;
            }
            catch (Exception error) when (error is IOException or CryptographicException or JsonException or UnauthorizedAccessException)
            { failed = true; }
            finally { if (plaintext != null) CryptographicOperations.ZeroMemory(plaintext); }
        }
        if (failed) throw new InvalidDataException("无法解密登录备份，原文件已保留。请使用原 Windows 用户，或重新登录后保存。");
        return null;
    }

    internal static List<SessionCookie> MissingCookies(SavedLoginSession session, IEnumerable<SessionCookie> current, DateTimeOffset now)
    {
        var existing = current.Where(cookie => IsUsable(cookie, now)).Select(cookie => cookie.Key).ToHashSet();
        return session.Cookies.Where(cookie => IsUsable(cookie, now) && existing.Add(cookie.Key)).ToList();
    }

    internal void Clear()
    {
        foreach (var candidate in new[] { path, path + ".bak", path + ".tmp" }) File.Delete(candidate);
    }
}
