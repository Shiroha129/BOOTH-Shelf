using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;

namespace BoothShelf;

internal static class CurrencyScheduleSelfTest
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class RateHandler : HttpMessageHandler
    {
        internal int Calls;
        internal bool Fail;
        internal TaskCompletionSource? Pending;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            if (Pending != null) await Pending.Task.WaitAsync(token);
            if (Fail) throw new HttpRequestException("test offline");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"base":"JPY","date":"2026-11-06","rates":{"CNY":0.042137,"USD":0.0064}}""")
            };
        }
    }

    internal static async Task<JsonObject> RunAsync(string root)
    {
        var localNow = new DateTimeOffset(2026, 11, 10, 23, 59, 50, TimeSpan.FromHours(8));
        using var handler = new RateHandler();
        using var currency = new CurrencyService(root, handler);
        await currency.RefreshAsync(false, CancellationToken.None);
        var schedule = new CurrencyRefreshScheduler(currency, () => localNow);
        var published = 0;
        schedule.Refreshed += _ => published++;
        await schedule.RefreshAsync(false, CancellationToken.None);
        Check(handler.Calls == 2 && published == 1, "启动未绕过当天汇率缓存");
        Check(schedule.NextDelayMilliseconds == 10000, "未按本地零点安排检查");
        await schedule.RefreshAsync(false, CancellationToken.None);
        Check(handler.Calls == 2, "同一日期重复请求");
        localNow = localNow.AddSeconds(10);
        Check(schedule.IsDue, "本地零点未触发");
        await schedule.RefreshAsync(false, CancellationToken.None);
        Check(handler.Calls == 3 && !schedule.IsDue, "零点未强制联网或重复触发");
        localNow = localNow.AddHours(8);
        await schedule.RefreshAsync(false, CancellationToken.None);
        Check(handler.Calls == 3, "同日唤醒重复更新");
        localNow = localNow.AddDays(3);
        await schedule.RefreshAsync(false, CancellationToken.None);
        Check(handler.Calls == 4, "跨日唤醒未合并补更新");
        localNow = localNow.AddDays(1);
        handler.Fail = true;
        var cachedFile = File.ReadAllText(Path.Combine(root, "exchange-rates.json"));
        var failed = await schedule.RefreshAsync(false, CancellationToken.None);
        Check(failed["error"] != null && failed["rates"]!["CNY"]!.GetValue<decimal>() == 0.042137m
            && File.ReadAllText(Path.Combine(root, "exchange-rates.json")) == cachedFile, "后台失败丢失原缓存");
        localNow = localNow.AddMinutes(14);
        Check(!schedule.IsDue, "失败后频繁重试");
        await schedule.RefreshAsync(false, CancellationToken.None);
        Check(handler.Calls == 5, "重试时间前发起请求");
        localNow = localNow.AddMinutes(1);
        Check(schedule.IsDue, "十五分钟后未重试");
        handler.Fail = false;
        await schedule.RefreshAsync(false, CancellationToken.None);
        Check(handler.Calls == 6 && schedule.Current["error"] == null, "恢复网络后错误未清除");
        handler.Pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        localNow = localNow.AddDays(1);
        var automatic = schedule.RefreshAsync(false, CancellationToken.None);
        var manual = schedule.RefreshAsync(true, CancellationToken.None);
        var timer = schedule.RefreshAsync(false, CancellationToken.None);
        Check(ReferenceEquals(automatic, manual) && ReferenceEquals(automatic, timer) && handler.Calls == 7, "手动和后台更新未合并");
        handler.Pending.SetResult();
        await Task.WhenAll(automatic, manual, timer);
        handler.Pending = null;
        localNow = localNow.ToOffset(TimeSpan.FromHours(-5));
        Check(schedule.IsDue, "切换时区后的本地日期未检测");
        await schedule.RefreshAsync(false, CancellationToken.None);
        Check(handler.Calls == 8, "切换时区未更新");
        var restarted = new CurrencyRefreshScheduler(currency, () => localNow);
        await restarted.RefreshAsync(false, CancellationToken.None);
        Check(handler.Calls == 9, "同日重新启动未联网更新");
        handler.Pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var canceled = schedule.RefreshAsync(true, cancellation.Token);
        cancellation.Cancel();
        var cancellationObserved = false;
        try { await canceled; }
        catch (OperationCanceledException) { cancellationObserved = true; }
        Check(cancellationObserved && schedule.Current["error"] == null, "退出未取消请求或污染缓存");
        handler.Pending = null;
        localNow = new DateTimeOffset(2026, 11, 20, 23, 59, 59, 500, TimeSpan.FromHours(5.5));
        await schedule.RefreshAsync(false, CancellationToken.None);
        Check(schedule.NextDelayMilliseconds == 500, "非整点时区的零点安排错误");
        var slow = new CurrencyRefreshScheduler(currency, () => localNow);
        handler.Pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var spanningMidnight = slow.RefreshAsync(false, CancellationToken.None);
        localNow = localNow.AddSeconds(1);
        handler.Pending.SetResult();
        await spanningMidnight;
        Check(slow.IsDue, "零点前开始的慢请求吞掉了次日更新");
        handler.Pending = null;
        await slow.RefreshAsync(false, CancellationToken.None);
        Check(!slow.IsDue, "跨零点请求未补查");
        return new JsonObject
        {
            ["startupBypassesFreshCache"] = true, ["localMidnight"] = true, ["noRepeatedDailyRequests"] = true,
            ["resumeCatchup"] = true, ["offlineCacheAndRetry"] = true, ["concurrentRefreshCoalesced"] = true,
            ["timeZoneChange"] = true, ["restartSameDay"] = true, ["exitCancellation"] = true,
            ["halfHourTimeZone"] = true, ["slowRequestAcrossMidnight"] = true
        };
    }
}
