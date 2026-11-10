using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;

namespace BoothShelf;

internal static class RuntimeInstallerSelfTest
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(respond(request));
        }
    }

    private sealed class Progress(Action<string> update) : IProgress<string>
    {
        public void Report(string value) => update(value);
    }

    internal static async Task<JsonObject> RunAsync(string root)
    {
        var requests = 0;
        var executions = 0;
        var installed = false;
        HttpResponseMessage Response(HttpRequestMessage request)
        {
            requests++;
            Check(request.RequestUri!.ToString() == RuntimeInstaller.DownloadUrl, "安装器来源不是微软官方地址");
            return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent([1, 2, 3]) };
        }
        Task<int> Execute(string target)
        {
            Check(File.ReadAllBytes(target).SequenceEqual(new byte[] { 1, 2, 3 }), "安装器下载不完整");
            executions++;
            installed = true;
            return Task.FromResult(0);
        }
        var quiet = new Progress(_ => { });
        using (var present = new RuntimeInstaller(new Handler(Response), _ => true, Execute, () => true))
            await present.InstallAsync(root, quiet, CancellationToken.None);
        Check(requests == 0 && executions == 0, "已有运行时仍下载或执行安装器");
        using (var invalid = new RuntimeInstaller(new Handler(Response), _ => false, Execute, () => false))
            await RejectAsync(() => invalid.InstallAsync(root, quiet, CancellationToken.None));
        Check(executions == 0, "签名无效仍执行安装器");
        using (var valid = new RuntimeInstaller(new Handler(Response), _ => true, Execute, () => installed))
            await valid.InstallAsync(root, quiet, CancellationToken.None);
        Check(installed && executions == 1 && !Directory.EnumerateDirectories(root, "runtime-setup-*").Any(), "安装完成未确认或残留安装包");
        using var cancellation = new CancellationTokenSource();
        using (var canceled = new RuntimeInstaller(new Handler(Response), _ => true, Execute, () => false))
            await RejectAsync(() => canceled.InstallAsync(root, new Progress(_ => cancellation.Cancel()), cancellation.Token));
        Check(executions == 1, "取消后仍执行安装器");
        using (var missing = new RuntimeInstaller(new Handler(Response), _ => true, _ => Task.FromResult(0), () => false))
            await RejectAsync(() => missing.InstallAsync(root, quiet, CancellationToken.None));
        using (var oversized = new RuntimeInstaller(new Handler(request =>
        {
            var response = Response(request);
            response.Content.Headers.ContentLength = 40 * 1024 * 1024;
            return response;
        }), _ => true, Execute, () => false))
            await RejectAsync(() => oversized.InstallAsync(root, quiet, CancellationToken.None));
        Check(executions == 1, "超大安装器仍被执行");
        var unsigned = Path.Combine(root, "unsigned.exe");
        File.WriteAllText(unsigned, "synthetic unsigned fixture");
        Check(!RuntimeInstaller.VerifyMicrosoftSignature(unsigned), "未签名文件通过 WinVerifyTrust");
        return new JsonObject { ["existingRuntimeSkipped"] = true, ["officialUrl"] = true, ["signatureRequired"] = true,
            ["cancellation"] = true, ["installationRechecked"] = true, ["sizeLimited"] = true, ["temporaryFilesCleaned"] = true,
            ["unsignedFileRejected"] = true, ["realInstallerNotExecuted"] = true };
    }

    private static async Task RejectAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException or OperationCanceledException) { return; }
        throw new Exception("无效安装请求未被拒绝");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
