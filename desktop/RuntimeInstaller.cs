using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Web.WebView2.Core;

namespace BoothShelf;

internal sealed class RuntimeInstaller : IDisposable
{
    internal const string DownloadUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";
    internal const string ManualUrl = "https://developer.microsoft.com/microsoft-edge/webview2/consumer/";
    private const long MaximumBytes = 32 * 1024 * 1024;
    private readonly HttpClient client;
    private readonly Func<string, bool> verify;
    private readonly Func<string, Task<int>> execute;
    private readonly Func<bool> available;

    internal RuntimeInstaller(HttpMessageHandler? handler = null, Func<string, bool>? verify = null,
        Func<string, Task<int>>? execute = null, Func<bool>? available = null)
    {
        client = handler == null ? new HttpClient() : new HttpClient(handler);
        client.Timeout = TimeSpan.FromMinutes(3);
        this.verify = verify ?? VerifyMicrosoftSignature;
        this.execute = execute ?? ExecuteAsync;
        this.available = available ?? IsAvailable;
    }

    internal static bool IsAvailable()
    {
        try { return !string.IsNullOrWhiteSpace(CoreWebView2Environment.GetAvailableBrowserVersionString()); }
        catch (WebView2RuntimeNotFoundException) { return false; }
    }

    internal async Task InstallAsync(string root, IProgress<string> progress, CancellationToken token)
    {
        if (available()) return;
        var directory = Path.Combine(root, "runtime-setup-" + Guid.NewGuid().ToString("N"));
        var installer = Path.Combine(directory, "MicrosoftEdgeWebview2Setup.exe");
        Directory.CreateDirectory(directory);
        try
        {
            using var downloadCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            downloadCancellation.CancelAfter(TimeSpan.FromMinutes(3));
            var downloadToken = downloadCancellation.Token;
            progress.Report("正在下载微软官方安装器…");
            using (var response = await client.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, downloadToken))
            {
                response.EnsureSuccessStatusCode();
                if (response.RequestMessage?.RequestUri?.Scheme != "https" || response.Content.Headers.ContentLength > MaximumBytes)
                    throw new InvalidDataException("安装器下载无效，请重试或使用手动下载。");
                await using var input = await response.Content.ReadAsStreamAsync(downloadToken);
                await using var output = new FileStream(installer, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                var buffer = new byte[81920];
                long total = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, downloadToken)) > 0)
                {
                    total += count;
                    if (total > MaximumBytes) throw new InvalidDataException("安装器下载无效，请重试或使用手动下载。");
                    await output.WriteAsync(buffer.AsMemory(0, count), downloadToken);
                }
                if (total == 0 || response.Content.Headers.ContentLength is long expected && total != expected)
                    throw new InvalidDataException("安装器下载无效，请重试或使用手动下载。");
            }
            token.ThrowIfCancellationRequested();
            progress.Report("正在验证微软数字签名…");
            if (!verify(installer)) throw new InvalidDataException("无法验证安装器的微软数字签名，已停止安装。");
            token.ThrowIfCancellationRequested();
            progress.Report("正在安装 WebView2，请稍候…");
            var exitCode = await execute(installer);
            if (!available()) throw new InvalidOperationException($"安装后仍未检测到 WebView2（代码 {exitCode}）。请重试，或手动安装后重新打开软件。");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("下载超时，请检查网络后重试，或使用手动下载。");
        }
        finally
        {
            try { File.Delete(installer); Directory.Delete(directory); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static async Task<int> ExecuteAsync(string installer)
    {
        var start = new ProcessStartInfo(installer) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("/silent");
        start.ArgumentList.Add("/install");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 WebView2 安装器。");
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(10)); }
        catch (TimeoutException) { throw new InvalidOperationException("安装仍未结束，请稍后重新打开软件，或使用手动下载。"); }
        return process.ExitCode;
    }

    internal static bool VerifyMicrosoftSignature(string target)
    {
        var filename = Marshal.StringToCoTaskMemUni(target);
        var infoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<TrustFile>());
        try
        {
            var info = new TrustFile { Size = (uint)Marshal.SizeOf<TrustFile>(), Filename = filename };
            Marshal.StructureToPtr(info, infoPointer, false);
            var data = new TrustData { Size = (uint)Marshal.SizeOf<TrustData>(), UiChoice = 2, UnionChoice = 1, File = infoPointer };
            var action = new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
            if (WinVerifyTrust(new IntPtr(-1), ref action, ref data) != 0) return false;
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(target));
            return certificate.GetNameInfo(X509NameType.SimpleName, false) == "Microsoft Corporation";
        }
        catch (System.Security.Cryptography.CryptographicException) { return false; }
        finally { Marshal.FreeHGlobal(infoPointer); Marshal.FreeCoTaskMem(filename); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrustFile
    {
        internal uint Size;
        internal IntPtr Filename;
        internal IntPtr File;
        internal IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData
    {
        internal uint Size;
        internal IntPtr PolicyCallback;
        internal IntPtr SipClient;
        internal uint UiChoice;
        internal uint RevocationChecks;
        internal uint UnionChoice;
        internal IntPtr File;
        internal uint StateAction;
        internal IntPtr StateData;
        internal IntPtr UrlReference;
        internal uint ProviderFlags;
        internal uint UiContext;
    }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);

    public void Dispose() => client.Dispose();
}
