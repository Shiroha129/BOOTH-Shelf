using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;

namespace BoothShelf;

internal static class DiagnosticLogSelfTest
{
    internal static JsonObject Run(string root)
    {
        var log = new DiagnosticLog(root, 2048);
        const string secret = "DO-NOT-LOG-credential";
        var error = new HttpRequestException("https://example.test/private?token=" + secret,
            new IOException("Cookie: " + secret + " C:\\private\\account.json"), HttpStatusCode.BadGateway);
        log.Record("sync.orders.detail", "error", DiagnosticFailure.CodeFor("sync.orders.detail", error), error, page: 1);
        var path = Path.Combine(log.DirectoryPath, "app.log");
        var text = File.ReadAllText(path);
        var entry = JsonNode.Parse(text)!;
        Check(!text.Contains(secret) && !text.Contains("example.test") && !text.Contains("account.json") && !text.Contains("Cookie"), "诊断日志包含凭据、网址或本地路径");
        Check(entry["step"]!.GetValue<string>() == "sync.orders.detail" && entry["code"]!.GetValue<string>() == "BS-SYNC-ORDERS-DETAIL-NETWORK"
            && entry["errors"]![0]!["httpStatus"]!.GetValue<int>() == 502 && entry["errors"]!.AsArray().Count == 2, "诊断日志缺少出错阶段或网络状态");
        for (var index = 0; index < 60; index++) log.Record("sync.library.bought", "completed", page: index, count: 80);
        var files = Directory.GetFiles(log.DirectoryPath, "*.log");
        Check(files.Length == 3 && files.All(file => new FileInfo(file).Length <= 2048), "诊断日志未轮换或超出大小限制");
        foreach (var file in files)
            foreach (var line in File.ReadLines(file)) Check(JsonNode.Parse(line)?["time"] != null, "轮换后日志损坏");

        var blocked = Path.Combine(root, "blocked");
        File.WriteAllText(blocked, "not a directory");
        new DiagnosticLog(blocked).Record("startup", "started");
        var original = NativeLanguage.Preference;
        try
        {
            var retryable = new SyncReadException("BOOTH 请求超时", true);
            DiagnosticFailure.Note("sync.library.free", retryable, page: 2);
            var contextual = DiagnosticFailure.Capture("sync", new IOException("synthetic retry limit", retryable));
            Check(retryable.Retryable && contextual.Code == "BS-SYNC-LIBRARY-FREE-NETWORK"
                && contextual.Message.Contains("第 2 页"), "诊断信息丢失了原始异常类型、来源或页数");
            var failure = DiagnosticFailure.Capture("sync.orders.detail", new SyncReadException("BOOTH 请求超时", true));
            Check(ReferenceEquals(failure, DiagnosticFailure.Capture("sync", failure)), "错误被重复包装，丢失原始步骤");
            Check(failure.Code == "BS-SYNC-ORDERS-DETAIL-NETWORK" && failure.Step == "sync.orders.detail", "错误编号无法定位阶段");
            foreach (var language in new[] { "zh-CN", "zh-TW", "ja", "en", "ko" })
            {
                NativeLanguage.Preference = language;
                var translated = NativeLanguage.Message(failure.Message);
                Check(translated.Contains(failure.Code) && translated.Contains(NativeLanguage.Text("核对订单金额"))
                    && translated.Contains(NativeLanguage.Text("BOOTH 请求超时")), "诊断错误未完整翻译");
            }
        }
        finally { NativeLanguage.Preference = original; }
        return new JsonObject { ["stageAndErrorCode"] = true, ["networkStatus"] = true, ["noPrivatePayloads"] = true,
            ["boundedRotation"] = true, ["loggingFailureIsNonFatal"] = true, ["fiveLanguages"] = true };
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
