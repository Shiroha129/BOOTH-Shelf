using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace BoothShelf;

internal static class BoothRequestPolicySelfTest
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Handler : HttpMessageHandler
    {
        internal int Calls;
        internal int Active;
        internal int Peak;
        internal HttpStatusCode Status = HttpStatusCode.OK;
        internal string? Html;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            Peak = Math.Max(Peak, Interlocked.Increment(ref Active));
            try
            {
                await Task.Delay(20, token);
                var id = Path.GetFileNameWithoutExtension(request.RequestUri!.AbsolutePath);
                var response = new HttpResponseMessage(Status)
                {
                    Content = Html == null ? new StringContent("{\"id\":\"" + id + "\",\"tags\":[],\"variations\":[]}") : new StringContent(Html)
                };
                if (Html != null) response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/html");
                if ((int)Status == 429) response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(20));
                return response;
            }
            finally { Interlocked.Decrement(ref Active); }
        }
    }

    private static async Task<bool> IsPaused(Func<Task> action)
    {
        try { await action(); return false; }
        catch (BoothAccessPausedException) { return true; }
    }

    internal static async Task<JsonObject> RunAsync(string root)
    {
        var time = DateTimeOffset.Parse("2026-11-10T01:00:00Z");
        var starts = new List<DateTimeOffset>();
        var pacing = new BoothRequestPolicy(clock: () => time, delay: (milliseconds, token) =>
        {
            token.ThrowIfCancellationRequested();
            time = time.AddMilliseconds(milliseconds);
            return Task.CompletedTask;
        });
        for (var index = 0; index < 5; index++)
            await pacing.RunAsync(() => { starts.Add(time); return Task.FromResult(true); }, CancellationToken.None);
        Check(starts.Zip(starts.Skip(1)).All(pair => pair.Second - pair.First >= TimeSpan.FromMilliseconds(250)), "请求启动没有间隔");
        var access = new BoothRequestPolicy(root, 0, () => time);
        var handler = new Handler();
        using var client = new HttpClient(handler, false);
        using var metadata = new MetadataCatalog(root, handler, access);
        var items = new JsonArray(Enumerable.Range(1, 8).Select(id => (JsonNode)new JsonObject { ["id"] = id.ToString() }).ToArray());
        async Task ReadApi(string id)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.booth.pm/vroid/items/" + id);
            using var response = await access.SendAsync(client, request, CancellationToken.None);
        }
        await Task.WhenAll(metadata.EnrichAsync(items, CancellationToken.None), ReadApi("100"), ReadApi("101"));
        Check(handler.Peak == 4 && handler.Calls == 10, "标签与 API 请求没有共用总并发上限");
        handler.Status = HttpStatusCode.NotFound;
        await ReadApi("404");
        Check(!access.Paused, "单件商品下架误触发全局暂停");
        handler.Status = (HttpStatusCode)429;
        Check(await IsPaused(() => ReadApi("429")) && access.Paused, "429 没有暂停所有自动请求");
        var calls = handler.Calls;
        Check(await IsPaused(() => metadata.EnrichAsync(new JsonArray(new JsonObject { ["id"] = "300" }), CancellationToken.None)) && handler.Calls == calls,
            "API 限流后标签仍向网站发出请求");
        var restored = new BoothRequestPolicy(root, 0, () => time);
        Check(restored.Paused && DateTimeOffset.Parse(restored.State()["retryAt"]!.ToString()) == time.AddMinutes(20), "暂停状态或 Retry-After 没有跨重启保存");
        var denied = false;
        try { restored.Resume(); } catch (InvalidOperationException) { denied = true; }
        Check(denied, "手动继续绕过了网站要求的冷却时间");
        time = time.AddMinutes(21);
        Check(restored.Paused && access.Paused, "冷却结束后未经手动确认恢复请求");
        access.Resume();
        restored.Resume();
        Check(!new BoothRequestPolicy(root).Paused, "手动恢复未保存");
        handler.Status = HttpStatusCode.Forbidden;
        Check(await IsPaused(() => ReadApi("403")), "403 没有暂停");
        time = time.AddMinutes(6);
        access.Resume();
        handler.Status = HttpStatusCode.OK;
        handler.Html = "<title>Just a moment...</title><form id='challenge-form'></form>";
        Check(await IsPaused(() => ReadApi("200")), "HTTP 200 验证页没有暂停");
        Check(BoothRequestPolicy.IsChallenge("<script src='/cdn-cgi/challenge-platform/test'></script>")
            && !BoothRequestPolicy.IsChallenge("<title>Library</title><p>A product about captcha</p>"), "验证检测误判普通商品文字");
        var queuedAccess = new BoothRequestPolicy(intervalMilliseconds: 0);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = Enumerable.Range(0, 4).Select(_ => queuedAccess.RunAsync(() => release.Task, CancellationToken.None)).ToArray();
        var sent = false;
        var queued = queuedAccess.RunAsync(() => { sent = true; return Task.FromResult(true); }, CancellationToken.None);
        try { queuedAccess.Inspect(403); } catch (BoothAccessPausedException) { }
        release.SetResult(true);
        await Task.WhenAll(running);
        Check(await IsPaused(async () => await queued) && !sent, "已排队的请求在暂停后仍被发出");
        return new JsonObject { ["sharedConcurrency"] = 4, ["startSpacingMs"] = 250, ["persistentPause"] = true,
            ["retryAfterHonored"] = true, ["manualResumeRequired"] = true, ["challengeAnd403"] = true, ["queuedRequestsBlocked"] = true };
    }
}
