using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace BoothShelf;

internal sealed class DiagnosticLog(string root, long maxBytes = 1024 * 1024)
{
    private readonly object gate = new();
    internal string DirectoryPath { get; } = Path.Combine(root, "logs");

    internal void Record(string step, string status, string? code = null, Exception? error = null,
        int? page = null, int? count = null, string? reason = null)
    {
        try
        {
            var errors = new List<object>();
            for (var current = error; current != null && errors.Count < 8; current = current.InnerException)
                errors.Add(new
                {
                    type = current.GetType().FullName, hresult = current.HResult.ToString("X8"),
                    at = current.TargetSite?.DeclaringType?.FullName + "." + current.TargetSite?.Name,
                    httpStatus = (int?)(current as HttpRequestException)?.StatusCode,
                    retryable = (current as SyncReadException)?.Retryable
                });
            // Log only diagnostic fields, never exception messages, URLs, page bodies or request payloads.
            var line = JsonSerializer.Serialize(new
            {
                time = DateTimeOffset.UtcNow.ToString("O"), version = Program.AppVersion.ToString(3),
                step, status, code, page, count, reason, errors
            }) + Environment.NewLine;
            lock (gate)
            {
                Directory.CreateDirectory(DirectoryPath);
                var path = Path.Combine(DirectoryPath, "app.log");
                if (File.Exists(path) && new FileInfo(path).Length + Encoding.UTF8.GetByteCount(line) > maxBytes)
                {
                    File.Delete(Path.Combine(DirectoryPath, "app.2.log"));
                    var previous = Path.Combine(DirectoryPath, "app.1.log");
                    if (File.Exists(previous)) File.Move(previous, Path.Combine(DirectoryPath, "app.2.log"));
                    File.Move(path, previous);
                }
                File.AppendAllText(path, line, new UTF8Encoding(false));
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

internal sealed class DiagnosticFailure : Exception
{
    internal string Code { get; }
    internal string Step { get; }

    private DiagnosticFailure(string step, Exception error, string? displayMessage, int? page)
        : base(Format(step, error, displayMessage, page), error)
    {
        Step = step;
        Code = CodeFor(step, error);
    }

    internal static DiagnosticFailure Capture(string step, Exception error, string? displayMessage = null, int? page = null)
    {
        if (error is DiagnosticFailure existing) return existing;
        for (var current = error; current != null; current = current.InnerException)
            if (current.Data["BOOTH.Step"] is string context)
            {
                step = context;
                page = current.Data["BOOTH.Page"] as int? ?? page;
                break;
            }
        var failure = new DiagnosticFailure(step, error, displayMessage, page);
        Program.Diagnostics?.Record(step, "error", failure.Code, error, page: page);
        return failure;
    }

    internal static void Note(string step, Exception error, int? page = null)
    {
        error.Data["BOOTH.Step"] = step;
        if (page != null) error.Data["BOOTH.Page"] = page;
        Program.Diagnostics?.Record(step, "failed-attempt", CodeFor(step, error), error, page: page);
    }

    internal static string ActionStep(string? action) => action switch
    {
        "sync" or "pause" => "sync",
        "login" or "auth-check" or "booth-access-resume" => "account.authentication",
        "session-save" or "session-remember" => "account.session",
        "files" or "downloads-start" or "downloads-cancel" or "updates-download" => "download.prepare",
        "product" => "product.details",
        "save-product-image" or "copy-product-image" => "product.image",
        "currency-refresh" => "currency",
        "app-update-check" or "app-update-release" => "app-update.check",
        "app-update-start" or "app-update-cancel" or "app-update-apply" => "app-update.install",
        "updates-check" or "updates-list" or "updates-read" => "notifications",
        "local-list" or "local-action" or "local-asset-folders" or "local-asset-delete" or "open-download-folder" => "local.files",
        "load" or "settings-save" or "choose-directory" or "export" or "open-diagnostics" => "storage",
        _ => "app"
    };

    internal static string Label(string step) => step switch
    {
        "startup" => "启动程序",
        "account.authentication" or "sync.authentication" => "检查 BOOTH 登录",
        "account.session" or "sync.session" => "保存登录会话",
        "sync.library.bought" => "读取已购商品",
        "sync.library.gift" => "读取礼物商品",
        "sync.library.free" => "读取免费商品",
        "sync.library" => "读取商品列表",
        "sync.metadata" or "product.details" => "读取商品资料",
        "sync.orders.list" => "读取订单列表",
        "sync.orders.detail" => "核对订单金额",
        "sync.storage" or "storage" => "保存或读取本地数据",
        "sync.currency" or "currency" => "更新汇率",
        "sync" => "同步商品",
        "download.prepare" => "读取下载文件",
        "download.transfer" => "下载文件",
        "local.files" => "操作本地文件",
        "product.image" => "保存或复制图片",
        "app-update.check" => "检查软件更新",
        "app-update.download" => "下载软件更新",
        "app-update.install" => "安装软件更新",
        "notifications" => "检查资产更新",
        _ => "执行操作"
    };

    private static string Category(Exception error) => error switch
    {
        BoothAccessPausedException => "ACCESS",
        IOException { InnerException: SyncReadException { Retryable: true } or HttpRequestException } => "NETWORK",
        UnauthorizedAccessException => "PERMISSION",
        TimeoutException or HttpRequestException or TaskCanceledException or SyncReadException { Retryable: true } => "NETWORK",
        SyncReadException => "PAGE",
        System.Text.Json.JsonException or InvalidDataException => "DATA",
        IOException => "IO",
        _ => "FAILED"
    };

    internal static string CodeFor(string step, Exception error) => "BS-" + step.Replace('.', '-').ToUpperInvariant() + "-" + Category(error);

    private static string Format(string step, Exception error, string? displayMessage, int? page)
    {
        var hint = Category(error) switch
        {
            "ACCESS" or "PAGE" => "请打开 BOOTH 窗口检查登录和网页验证，再到设置中继续访问。",
            "NETWORK" => "请检查网络后重试；若仍失败，可在设置中打开诊断日志。",
            "PERMISSION" or "IO" => "请检查目录权限和可用空间；可在设置中打开诊断日志。",
            _ => "可在设置中打开诊断日志，并提供错误编号以便排查。"
        };
        var stage = page == null ? Label(step) : $"{Label(step)}（第 {page} 页）";
        return $"[{CodeFor(step, error)}] {stage}：{displayMessage ?? error.Message} · {hint}";
    }
}
